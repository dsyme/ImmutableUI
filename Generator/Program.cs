using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Newtonsoft.Json;

namespace Generator
{
    class Bindings
    {
        // Input
        public List<string> Assemblies { get; set; }
        public List<TypeBinding> Types { get; set; }
        public string OutputNamespace { get; set; }

        // Output
        public List<AssemblyDefinition> AssemblyDefinitions { get; set; }

        public TypeDefinition GetTypeDefinition(string name) =>
            (from a in AssemblyDefinitions
             from m in a.Modules
             from t in m.Types
             where t.FullName == name
             select t).First();

        public TypeBinding FindType (string name) => Types.FirstOrDefault (x => x.Name == name);
    }

    class TypeBinding
    {
        // Input
        public string Name { get; set; }
        public List<MemberBinding> Members { get; set; }

        // Output
        public string BoundCode { get; set; }
        public TypeDefinition Definition { get; set; }

        public string BoundName => "XamlElement"; // Definition.Name + "Description";
    }

    class MemberBinding
    {
        // Input
        public string Name { get; set; }
        public string Unique { get; set; }
        public string Default { get; set; }
        public string ConstDefault { get; set; }
        public string Apply { get; set; }
        public string Equality { get; set; }

        // Output
        public MemberReference Definition { get; set; }

        public TypeReference BoundType =>
            (Definition is PropertyDefinition p) ?
                p.PropertyType : 
                ((EventDefinition)Definition).EventType;

        public string UniqueName => string.IsNullOrEmpty(Unique) ? Name : Unique;
        public string LowerUniqueName => char.ToLowerInvariant (UniqueName[0]) + UniqueName.Substring (1);

        public string BoundConstDefault => string.IsNullOrEmpty(ConstDefault) ? Default : ConstDefault;
    }

    class Program
    {
        static string nl => Environment.NewLine;
        static int Main(string[] args)
        {
            try {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("usage: generator <outputPath>");
                    Environment.Exit(1);
                }
                var bindingsPath = args[0];
                var outputPath = args[1];

                var bindings = JsonConvert.DeserializeObject<Bindings> (File.ReadAllText (bindingsPath));

                bindings.AssemblyDefinitions = bindings.Assemblies.Select(LoadAssembly).ToList();

                foreach (var x in bindings.Types)
                    x.Definition = bindings.GetTypeDefinition (x.Name);
                foreach (var x in bindings.Types) {
                    foreach (var m in x.Members) {
                        if (FindProperty (m.Name, x.Definition) is PropertyDefinition p) {
                            m.Definition = p;
                        }
                        else if (FindEvent (m.Name, x.Definition) is EventDefinition e) {
                            m.Definition = e;
                        }
                        else {
                            throw new Exception ($"Could not find member `{m.Name}`");
                        }
                    }
                }
                var code = BindTypes (bindings);

                File.WriteAllText (outputPath, code);
                return 0;
            }
            catch (Exception ex) {
                System.Console.WriteLine(ex);
                return 1;
            }
        }

        static string GetName(Bindings bindings, TypeReference tref, IEnumerable<Tuple<TypeReference, TypeDefinition>> h)
        {
            if (tref.IsGenericParameter)
            {
                if (h != null)
                {
                    var r = ResolveGenericParameter(tref, h);
                    return GetName(bindings, r, h);
                }
                else
                {
                    return "XamlElement";
                }
            }
            if (tref.IsGenericInstance)
            {
                var n = tref.Name.Substring(0, tref.Name.IndexOf('`'));
                var ns = tref.Namespace;
                if (tref.IsNested)
                {
                    n = tref.DeclaringType.Name + "." + n;
                    ns = tref.DeclaringType.Namespace;
                }
                var args = string.Join(", ", ((GenericInstanceType)tref).GenericArguments.Select(s => GetName(bindings, s, h)));
                return $"{ns}.{n}<{args}>";
            }
            switch (tref.FullName)
            {
                case "System.String": return "string";
                case "System.Boolean": return "bool";
                case "System.Int32": return "int";
                case "System.Double": return "double";
                case "System.Single": return "single";
                default:
                    if (bindings.Types.FirstOrDefault(x => x.Name == tref.FullName) is TypeBinding tb)
                        return tb.BoundName;
                    return tref.FullName.Replace('/', '.');
            }
        }
        static string BindTypes (Bindings bindings)
        {
            var w = new StringWriter();
            var head = "";

            w.WriteLine("namespace " + bindings.OutputNamespace);
            w.WriteLine();
            w.WriteLine("#nowarn \"67\" // cast always holds");
            w.WriteLine();

            w.WriteLine($"/// A description of a visual element");
            w.WriteLine($"[<AllowNullLiteral>]");
            w.WriteLine($"type XamlElement(targetType: System.Type, create: (unit -> obj), apply: (XamlElement -> obj -> unit), attribs: Map<string, obj>) = ");
            w.WriteLine();
            w.WriteLine($"    /// Get the type created by the visual element");
            w.WriteLine($"    member x.TargetType = targetType");
            w.WriteLine();
            w.WriteLine($"    /// Get the attributes of the visual element");
            w.WriteLine($"    member x.Attributes = attribs");
            w.WriteLine();
            w.WriteLine($"    /// Apply the description to a visual element");
            w.WriteLine($"    member x.Apply (target: obj) = apply x target");
            w.WriteLine();
            w.WriteLine($"    /// Produce a new visual element with an adjusted attribute");
            w.WriteLine($"    member x.WithAttribute(name: string, value: obj) = XamlElement(targetType, create, apply, x.Attributes.Add(name, value))");
            var allMembersInAllTypesGroupedByName = (from type in bindings.Types from y in type.Members select y).ToList().GroupBy(y => y.UniqueName);
            foreach (var ms in allMembersInAllTypesGroupedByName)
            {
                var m = ms.First();
                w.WriteLine();
                w.WriteLine($"    /// Get the {m.UniqueName} property in the visual element");
                var boundConstDefault = m.BoundConstDefault.Replace("default(", "Unchecked.defaultof<").Replace(")", ">");
                w.WriteLine("    member __." + m.UniqueName + " = match attribs.TryFind(\"" + m.UniqueName + "\") with Some v -> unbox<" + GetName(bindings, m.BoundType, null) + ">(v) | None -> " + boundConstDefault);
            }
            foreach (var ms in allMembersInAllTypesGroupedByName)
            {
                var m = ms.First();
                w.WriteLine();
                w.WriteLine($"    /// Adjusts the {m.UniqueName} property in the visual element");
                w.WriteLine("    member __.With" + m.UniqueName + "(value: " + GetName(bindings, m.BoundType, null) + ") = XamlElement(targetType, create, apply, attribs.Add(\"" + m.UniqueName + "\", box value))");
            }

            foreach (var type in bindings.Types)
            {
                var t = type.Definition;
                var h = GetHierarchy(type.Definition).ToList();
                var ctor = t.Methods
                    .Where(x => x.IsConstructor && x.IsPublic)
                    .OrderBy(x => x.Parameters.Count)
                    .FirstOrDefault();

                w.WriteLine();
                w.WriteLine($"    /// Create a {t.FullName} from the view description");
                w.WriteLine($"    member x.Create{t.Name}() : {t.FullName} = (x.Create() :?> {t.FullName})");
            }

            w.WriteLine();
            w.WriteLine($"    /// Create the UI element from the view description");
            w.WriteLine($"    member x.Create() : obj =");
            w.WriteLine($"        let target = create()");
            w.WriteLine($"        x.Apply(target)");
            w.WriteLine($"        target");

            w.WriteLine();
            w.WriteLine($"type Xaml() = ");
            foreach (var type in bindings.Types)
            {
                var t = type.Definition;
                var h = GetHierarchy(type.Definition).ToList();
                var bh = h.Select(x => bindings.Types.FirstOrDefault(y => y.Name == x.Item2.FullName))
                            .Where(x => x != null)
                            .ToList();


                var baseType = bh.Count > 1 ? bh[1] : null;

                //
                // Properties
                //
                var allmembers = (from x in bh from y in x.Members select y).ToList();

                //
                // Constructor
                //
                w.WriteLine();
                w.WriteLine($"    /// Describes a {t.Name} in the view");
                w.Write($"    static member {t.Name}(");
                head = "";
                foreach (var m in allmembers)
                {
                    w.Write($"{head}?{m.LowerUniqueName}: {GetName(bindings, m.BoundType, null)}");
                    head = ", ";
                }
                w.WriteLine($") = ");
                w.WriteLine($"        let attribs = [| ");
                foreach (var m in allmembers)
                {
                    w.WriteLine("            match " + m.LowerUniqueName + " with | None -> () | Some v -> yield (\"" + m.UniqueName + "\"" + $", box v) ");
                }
                w.WriteLine($"          |]");

                var ctor = t.Methods
                    .Where(x => x.IsConstructor && x.IsPublic)
                    .OrderBy(x => x.Parameters.Count)
                    .FirstOrDefault();

                w.WriteLine($"        let create () =");
                if (!t.IsAbstract && ctor != null && ctor.Parameters.Count == 0)
                {
                    w.WriteLine($"            box (new {t.FullName}())");
                }
                else
                {
                    w.WriteLine($"            failwith \"can't create {t.FullName}\"");
                }
                w.WriteLine($"        let apply (this: XamlElement) (target:obj) = ");
                //var refToken = t.IsValueType ? "ref " : "";
                if (baseType == null && type.Members.Count() == 0)
                    w.WriteLine($"            ()");
                else
                {
                    w.WriteLine($"            let target = (target :?> {t.FullName})");
                    foreach (var m in allmembers)
                    {
                        var bt = ResolveGenericParameter(m.BoundType, h);
                        if (!string.IsNullOrEmpty(m.Apply))
                        {
                            w.WriteLine($"            {m.Apply}");
                        }
                        else if (GetListItemType(m.BoundType, h) is var etype && etype != null)
                        {
                            w.WriteLine($"            if (this.{m.UniqueName} = null || this.{m.UniqueName}.Count = 0) then");
                            w.WriteLine($"                match target.{m.Name} with");
                            w.WriteLine($"                | null -> ()");
                            w.WriteLine($"                | {m.LowerUniqueName} -> {m.LowerUniqueName}.Clear() ");
                            w.WriteLine($"            else");
                            w.WriteLine($"                while (target.{m.Name}.Count > this.{m.UniqueName}.Count) do target.{m.Name}.RemoveAt(target.{m.Name}.Count - 1)");
                            w.WriteLine($"                let n = target.{m.Name}.Count;");
                            w.WriteLine($"                for i in n .. this.{m.UniqueName}.Count-1 do");
                            w.WriteLine($"                    target.{m.Name}.Insert(i, this.{m.UniqueName}.[i].Create{etype.Name}())");
                            w.WriteLine($"                for i in 0 .. n - 1 do");
                            w.WriteLine($"                    this.{m.UniqueName}.[i].Apply(target.{m.Name}.[i])");
                        }
                        else
                        {
                            if (bindings.FindType(bt.FullName) is TypeBinding b)
                            {
                                if (bt.IsValueType)
                                {
                                    w.WriteLine($"            target.{m.Name} = this.{m.UniqueName}.Create{bt.Name}()");
                                }
                                else
                                {
                                    w.WriteLine($"            if (this.{m.UniqueName} <> Unchecked.defaultof<_>) then");
                                    w.WriteLine($"                match target.{m.Name} with");
                                    w.WriteLine($"                | :? {bt.FullName} as {m.LowerUniqueName} ->");
                                    w.WriteLine($"                    this.{m.UniqueName}.Apply({m.LowerUniqueName})");
                                    w.WriteLine($"                | _ ->");
                                    w.WriteLine($"                    target.{m.Name} <- this.{m.UniqueName}.Create{bt.Name}()");
                                    w.WriteLine($"            else");
                                    w.WriteLine($"                target.{m.Name} <- null;");
                                }
                            }
                            else
                            {
                                w.WriteLine($"            target.{m.Name} <- this.{m.UniqueName}");
                            }
                        }
                    }
                }
                                
                w.WriteLine($"        new XamlElement(typeof<{t.FullName}>, create, apply, Map.ofArray attribs)");

                /*
                 //
                                // Creates
                                //
                                w.WriteLine("");
                                w.WriteLine($"    /// Create the {t.FullName} from the view description");
                                w.WriteLine($"    abstract Create{t.Name}: unit -> {t.FullName}");
                                w.WriteLine("");
                                w.WriteLine($"    /// Create the {t.FullName} from the view description");
                                w.WriteLine($"    override this.Create{t.Name}() : {t.FullName} =");
                                if (t.IsAbstract || ctor == null || ctor.Parameters.Count != 0)
                                {
                                    w.WriteLine($"        raise (System.NotSupportedException(\"Cannot create {t.FullName} from \" + this.GetType().FullName))");
                                }
                                else
                                {
                                    w.WriteLine($"        let target = new {t.FullName}()");
                                    w.WriteLine($"        this.Apply(target)");
                                    w.WriteLine($"        target");
                                    foreach (var b in bh.Skip(1))
                                    {
                                        w.WriteLine("");
                                        w.WriteLine($"    /// Create the {t.FullName} from the view description");
                                        w.WriteLine($"    override this.Create{b.Definition.Name}() : {b.Definition.FullName} = this.Create{t.Name}() :> _");
                                    }
                                }

                                //
                                // Apply
                                //
                            }
                            */
            }
            return w.ToString ();
        }

        static TypeReference ResolveGenericParameter (TypeReference tref, IEnumerable<Tuple<TypeReference, TypeDefinition>> h)
        {
            if (tref == null)
                return null;
            if (!tref.IsGenericParameter)
                return tref;
            var q =
                from b in h where b.Item1.IsGenericInstance
                let ps = b.Item2.GenericParameters
                let p = ps.FirstOrDefault(x => x.Name == tref.Name)
                where p != null
                let pi = ps.IndexOf(p)
                let args = ((GenericInstanceType)b.Item1).GenericArguments
                select ResolveGenericParameter (args[pi], h);
            return q.First ();
        }

        static TypeReference GetListItemType (TypeReference tref, IEnumerable<Tuple<TypeReference, TypeDefinition>> h)
        {
            var r = ResolveGenericParameter (tref, h);
            if (r == null)
                return null;
            if (r.FullName == "System.String")
                return null;
            if (r.Name == "IList`1" && r.IsGenericInstance) {
                var args = ((GenericInstanceType)r).GenericArguments;
                return ResolveGenericParameter (args[0], h);
            }
            else {
                var bs = r.Resolve().Interfaces;
                return bs.Select (b => GetListItemType (b.InterfaceType, h)).FirstOrDefault(b => b != null);
            }
        }

        static PropertyDefinition FindProperty(string name, TypeDefinition type)
        {
            var q =
                from t in GetHierarchy(type)
                from p in t.Item2.Properties
                where p.Name == name
                select p;
            return q.FirstOrDefault ();
        }

        static EventDefinition FindEvent(string name, TypeDefinition type)
        {
            var q =
                from t in GetHierarchy(type)
                from p in t.Item2.Events
                where p.Name == name
                select p;
            return q.FirstOrDefault ();
        }

        static IEnumerable<Tuple<TypeReference, TypeDefinition>> GetHierarchy (TypeDefinition type)
        {
            var d = type;
            yield return Tuple.Create ((TypeReference)d, d);
            while (d.BaseType != null) {
                var r = d.BaseType;
                d = r.Resolve();
                yield return Tuple.Create (r, d);
            }
        }

        static AssemblyDefinition LoadAssembly (string path)
        {
            if (path.StartsWith("packages")) {
                var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = Path.Combine (user, ".nuget", path);
            }
            return AssemblyDefinition.ReadAssembly(path);
        }
    }
}
