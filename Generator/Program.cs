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

        public string BoundName => Definition.Name + "Description";
    }

    class MemberBinding
    {
        // Input
        public string Name { get; set; }
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

        public string LowerName => char.ToLowerInvariant (Name[0]) + Name.Substring (1);

        public string BoundConstDefault => string.IsNullOrEmpty(ConstDefault) ? Default : ConstDefault;
    }

    class Program
    {
        static bool fsharp = true;
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
                foreach (var x in bindings.Types)
                    BindType (x, bindings);

                var code =
                    fsharp
                    ? "namespace rec " + bindings.OutputNamespace + nl + nl +
                      String.Join (nl + nl, bindings.Types.Select(x => x.BoundCode))
                    : "namespace " + bindings.OutputNamespace + " {" + nl +
                      String.Join("" + nl + "", bindings.Types.Select(x => x.BoundCode)) +
                      "}" + nl;

                File.WriteAllText (outputPath, code);
                return 0;
            }
            catch (Exception ex) {
                System.Console.WriteLine(ex);
                return 1;
            }
        }

        static void BindType (TypeBinding type, Bindings bindings)
        {
            try {
                var t = type.Definition;
                var h = GetHierarchy (type.Definition).ToList ();
                var bh = h.Select (x => bindings.Types.FirstOrDefault(y => y.Name == x.Item2.FullName))
                          .Where (x => x != null)
                          .ToList ();

                string Name (TypeReference tref)
                {
                    if (tref.IsGenericParameter) {  
                        var r = ResolveGenericParameter (tref, h);                      
                        return Name (r);
                    }
                    if (tref.IsGenericInstance) {  
                        var n = tref.Name.Substring (0, tref.Name.IndexOf('`'));
                        var ns = tref.Namespace;
                        if (tref.IsNested) {
                            n = tref.DeclaringType.Name + "." + n;
                            ns = tref.DeclaringType.Namespace;
                        }
                        var args = string.Join(", ", ((GenericInstanceType)tref).GenericArguments.Select(Name));
                        return $"{ns}.{n}<{args}>";
                    }
                    switch (tref.FullName) {
                        case "System.String": return "string";
                        case "System.Boolean": return "bool";
                        case "System.Int32": return "int";
                        case "System.Double": return "double";
                        case "System.Single": return (fsharp ? "single" : "float");
                        default:
                            if (bindings.Types.FirstOrDefault (x => x.Name == tref.FullName) is TypeBinding tb)
                                return tb.BoundName;
                            return tref.FullName.Replace('/', '.');
                    }
                }

                var ctor = t.Methods
                    .Where (x => x.IsConstructor && x.IsPublic)
                    .OrderBy (x => x.Parameters.Count)
                    .FirstOrDefault ();

                var baseType = bh.Count > 1 ? bh[1] : null;

                //
                // Properties
                //
                var allmembers = (from x in bh from y in x.Members select y).ToList();

                var w = new StringWriter ();
                var head = "";

                if (fsharp)
                {
                    //
                    // Type + Constructor
                    //
                    w.WriteLine($"/// Represents a {type.BoundName} in the view desription");
                    w.WriteLine($"[<AllowNullLiteral>]");
                    w.Write($"type {type.BoundName}(");
                    head = "";
                    foreach (var m in allmembers)
                    {
                        w.Write($"{head}?{m.LowerName}");
                        head = ", ";
                    }
                    w.WriteLine($") = ");
                    if (baseType != null)
                    {
                        w.Write($"    inherit {baseType.BoundName}(");
                        head = "";
                        foreach (var b in bh.Skip(1))
                        {
                            foreach (var m in b.Members)
                            {
                                w.Write($"{head}?{m.LowerName}={m.LowerName}");
                                head = ", ";
                            }
                        }
                        w.WriteLine($")");
                    }

                    foreach (var m in type.Members)
                    {
                        w.WriteLine($"");
                        w.WriteLine($"    /// Gets the {m.Name} property of the {Name(m.BoundType)} in the view desription");
                        var boundConstDefault = m.BoundConstDefault.Replace("default(", "Unchecked.defaultof<").Replace(")", ">");
                        w.WriteLine($"    member val {m.Name} : {Name(m.BoundType)} = defaultArg {m.LowerName} {boundConstDefault}");
                    }
                    // With*
                    //
                    foreach (var m in allmembers)
                    {
                        var owner = bh.FirstOrDefault(x => x.Members.Contains(m));
                        //w.WriteLine($"");
                        //w.WriteLine($"    /// Adjusts the {m.Name} of the {type.BoundName} in the view desription");
                        //w.WriteLine($"    abstract With{m.Name}: {m.LowerName}: {Name(m.BoundType)} -> {type.BoundName}");
                        w.WriteLine($"");
                        w.WriteLine($"    /// Adjusts the {m.Name} of the {type.BoundName} in the view desription");
                        w.WriteLine($"    member this.With{m.Name}({m.LowerName}: {Name(m.BoundType)}) : {type.BoundName} =");
                        w.Write($"        {type.BoundName}(");
                        head = "";
                        foreach (var o in allmembers)
                        {
                            var v = o == m ? m.LowerName : "this." + o.Name;
                            w.Write($"{head}{o.LowerName}={v}");
                            head = ", ";
                        }
                        w.WriteLine(")");
                        //if (owner != type)
                       // {
                        //    w.WriteLine($"");
                        //    w.WriteLine($"    /// Adjusts the {m.Name} of the {type.BoundName} in the view desription");
                        //    w.WriteLine($"    override this.With{m.Name}({m.LowerName}: {Name(m.BoundType)}) : {owner.BoundName} =");
                        //    w.WriteLine($"        (this.With{m.Name}({m.LowerName}) : {type.BoundName}) :> {owner.BoundName}");
                       // }
                    }

                    //
                    // Equality
                    //

                    w.WriteLine($"");
                    w.WriteLine($"    /// Determines if two {type.BoundName} descriptions are equivalent");
                    w.WriteLine($"    override this.Equals(that: obj) =");
                    w.WriteLine($"        match that with");
                    w.WriteLine($"        | null -> false");
                    w.WriteLine($"        | :? {type.BoundName} as o ->");
                    w.Write($"            ");
                    if (allmembers.Count > 0)
                    {
                        head = "";
                        foreach (var m in allmembers)
                        {
                            if (!string.IsNullOrEmpty(m.Equality))
                            {
                                var equality = m.Equality.Replace("==", "=");
                                w.Write($"{head}{equality}");
                            }
                            else
                            {
                                w.Write($"{head}this.{m.Name} = o.{m.Name}");
                            }
                            head = " && ";
                        }
                    }
                    else
                    {
                        w.Write($"true");
                    }
                    w.WriteLine();
                    w.WriteLine($"        | _ -> false");

                    //
                    // Hash Code
                    //

                    w.WriteLine("");
                    w.WriteLine($"    /// Compute a hash code for the {type.BoundName} description");
                    w.WriteLine("    override this.GetHashCode() =");
                    if (baseType != null)
                        w.WriteLine($"        let mutable hc = base.GetHashCode()");
                    else
                        w.WriteLine($"        let mutable hc = 17");
                    foreach (var m in type.Members)
                    {
                        var bt = ResolveGenericParameter(m.BoundType, h);
                        if (bt.IsValueType)
                            w.WriteLine($"        hc <- hc * 37 + this.{m.Name}.GetHashCode()");
                        else
                            w.WriteLine($"        hc <- hc * 37 + hash this.{m.Name}");
                    }
                    w.WriteLine("        hc");

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
                    var refToken = t.IsValueType ? "ref " : "";
                    w.WriteLine("");
                    w.WriteLine($"    /// Apply the description to a {t.FullName}");
                    w.WriteLine($"    abstract Apply: target: {refToken}{t.FullName} -> unit");
                    w.WriteLine("");
                    w.WriteLine($"    /// Apply the description to a {t.FullName}");
                    w.WriteLine($"    override this.Apply(target: {refToken}{t.FullName}) : unit =");
                    if (baseType == null && type.Members.Count() == 0)
                        w.WriteLine($"        ()");
                    if (baseType != null)
                    {
                        var baseName = t.BaseType.FullName.Replace("`1", "").Replace("`2", "");
                        w.WriteLine($"        base.Apply(target :> {baseName})");
                    }
                    foreach (var m in type.Members)
                    {
                        var bt = ResolveGenericParameter(m.BoundType, h);
                        if (!string.IsNullOrEmpty(m.Apply))
                        {
                            w.WriteLine($"        {m.Apply}");
                        }
                        else if (GetListItemType(m.BoundType, h) is var etype && etype != null)
                        {
                            w.WriteLine($"        if (this.{m.Name} = null || this.{m.Name}.Count = 0) then");
                            w.WriteLine($"            match target.{m.Name} with");
                            w.WriteLine($"            | null -> ()");
                            w.WriteLine($"            | {m.LowerName} -> {m.LowerName}.Clear() ");
                            w.WriteLine($"        else");
                            w.WriteLine($"            while (target.{m.Name}.Count > this.{m.Name}.Count) do target.{m.Name}.RemoveAt(target.{m.Name}.Count - 1)");
                            w.WriteLine($"            let n = target.{m.Name}.Count;");
                            w.WriteLine($"            for i in n .. this.{m.Name}.Count-1 do");
                            w.WriteLine($"                target.{m.Name}.Insert(i, this.{m.Name}.[i].Create{etype.Name}())");
                            w.WriteLine($"            for i in 0 .. n - 1 do");
                            w.WriteLine($"                this.{m.Name}.[i].Apply(target.{m.Name}.[i])");
                        }
                        else
                        {
                            if (bindings.FindType(bt.FullName) is TypeBinding b)
                            {
                                if (bt.IsValueType)
                                {
                                    w.WriteLine($"        target.{m.Name} = this.{m.Name}.Create{bt.Name}()");
                                }
                                else
                                {
                                    w.WriteLine($"        if (this.{m.Name} <> Unchecked.defaultof<_>) then");
                                    w.WriteLine($"            match target.{m.Name} with");
                                    w.WriteLine($"            | :? {bt.FullName} as {m.LowerName} ->");
                                    w.WriteLine($"                this.{m.Name}.Apply({m.LowerName})");
                                    w.WriteLine($"            | _ ->");
                                    w.WriteLine($"                target.{m.Name} <- this.{m.Name}.Create{bt.Name}()");
                                    w.WriteLine($"        else");
                                    w.WriteLine($"            target.{m.Name} <- null;");
                                }
                            }
                            else
                            {
                                w.WriteLine($"        target.{m.Name} <- this.{m.Name}");
                            }
                        }
                    }
                    foreach (var b in bh.Skip(1))
                    {
                        w.WriteLine("");
                        w.WriteLine($"    /// Apply the description to a {b.Definition.FullName}");
                        w.WriteLine($"    override this.Apply(target: {b.Definition.FullName}) =");
                        w.WriteLine($"         match target with");
                        w.WriteLine($"         | :? {t.FullName} as t -> this.Apply(t)");
                        w.WriteLine($"         | _ -> base.Apply(target)");
                    }
                }
                else
                {
                    w.Write($"public partial class {type.BoundName}");
                    if (baseType != null)
                        w.WriteLine($" : {baseType.BoundName}");
                    else
                        w.WriteLine();
                    w.WriteLine("{");

                    foreach (var m in type.Members)
                    {
                        w.WriteLine($"\tpublic {Name(m.BoundType)} {m.Name} {{ get; }}");
                    }

                    //
                    // Constructor
                    //
                    w.Write($"\tpublic {type.BoundName}(");
                    foreach (var m in allmembers)
                    {
                        w.Write($"{head}{Name(m.BoundType)} {m.LowerName} = {m.BoundConstDefault}");
                        head = ", ";
                    }
                    w.Write($")");
                    if (baseType != null)
                    {
                        w.WriteLine();
                        w.Write("\t\t: base(");
                        head = "";
                        foreach (var b in bh.Skip(1))
                        {
                            foreach (var m in b.Members)
                            {
                                w.Write($"{head}{m.LowerName}");
                                head = ", ";
                            }
                        }
                        w.Write($")");
                    }
                    w.WriteLine(" {");
                    foreach (var m in type.Members)
                    {
                        var v = m.LowerName;
                        if (m.BoundConstDefault != m.Default)
                            v = $"{m.LowerName} == {m.ConstDefault} ? {m.Default} : {m.LowerName}";
                        w.WriteLine($"\t\t{m.Name} = {v};");
                    }
                    w.WriteLine("\t}");
                    
                    //
                    // With*
                    //
                    foreach (var m in allmembers)
                    {
                        var owner = bh.FirstOrDefault(x => x.Members.Contains(m));
                        if (owner == type)
                        {
                            w.Write($"\tpublic virtual {type.BoundName} With{m.Name}({Name(m.BoundType)} {m.LowerName}) => new {type.BoundName}(");
                        }
                        else
                        {
                            w.Write($"\tpublic override {owner.BoundName} With{m.Name}({Name(m.BoundType)} {m.LowerName}) => new {type.BoundName}(");
                        }
                        head = "";
                        foreach (var o in allmembers)
                        {
                            var v = o == m ? m.LowerName : o.Name;
                            w.Write($"{head}{v}");
                            head = ", ";
                        }
                        w.WriteLine(");");
                    }
                    
                    //
                    // Equality
                    //

                    w.WriteLine("\tpublic override bool Equals(object obj) {");
                    w.WriteLine($"\t\tif (obj == null || GetType() != obj.GetType()) return false;");
                    w.WriteLine($"\t\tvar o = ({type.BoundName})obj;");
                    w.Write("\t\treturn ");
                    if (allmembers.Count > 0)
                    {
                        head = "";
                        foreach (var m in allmembers)
                        {
                            if (!string.IsNullOrEmpty(m.Equality))
                                w.Write($"{head}{m.Equality}");
                            else
                                w.Write($"{head}{m.Name} == o.{m.Name}");
                            head = " && ";
                        }
                        w.WriteLine(";");
                    }
                    else
                    {
                        w.WriteLine("true;");
                    }
                    w.WriteLine("\t}");

                    //
                    // Hash Code
                    //

                    w.WriteLine("\tpublic override int GetHashCode() {");
                    if (baseType != null)
                        w.WriteLine($"\t\tvar hash = base.GetHashCode();");
                    else
                        w.WriteLine($"\t\tvar hash = 17;");
                    foreach (var m in type.Members)
                    {
                        var bt = ResolveGenericParameter(m.BoundType, h);
                        if (bt.IsValueType)
                            w.WriteLine($"\t\thash = hash * 37 + {m.Name}.GetHashCode();");
                        else
                            w.WriteLine($"\t\thash = hash * 37 + ({m.Name} != null ? {m.Name}.GetHashCode() : 0);");
                    }
                    w.WriteLine("\t\treturn hash;");
                    w.WriteLine("\t}");

                    //
                    // Creates
                    //
                    if (t.IsAbstract || ctor == null || ctor.Parameters.Count != 0)
                    {
                        w.WriteLine($"\tpublic virtual {t.FullName} Create{t.Name}() => throw new System.NotSupportedException(\"Cannot create {t.FullName} from \" + GetType().FullName);");
                    }
                    else
                    {
                        w.WriteLine($"\tpublic virtual {t.FullName} Create{t.Name}() {{");
                        w.WriteLine($"\t\tvar target = new {t.FullName}();");
                        w.WriteLine($"\t\tApply(target);");
                        w.WriteLine($"\t\treturn target;");
                        w.WriteLine("\t}");
                        foreach (var b in bh.Skip(1))
                        {
                            w.WriteLine($"\tpublic override {b.Definition.FullName} Create{b.Definition.Name}() => Create{t.Name}();");
                        }
                    }

                    //
                    // Apply
                    //
                    var refToken = t.IsValueType ? "ref " : "";
                    w.WriteLine($"\tpublic virtual void Apply({refToken}{t.FullName} target) {{");
                    if (baseType != null)
                        w.WriteLine($"\t\tbase.Apply(target);");
                    foreach (var m in type.Members)
                    {
                        var bt = ResolveGenericParameter(m.BoundType, h);
                        if (!string.IsNullOrEmpty(m.Apply))
                        {
                            w.WriteLine($"\t\t{m.Apply}");
                        }
                        else if (GetListItemType(m.BoundType, h) is var etype && etype != null)
                        {
                            w.WriteLine($"\t\tif ({m.Name} == null || {m.Name}.Count == 0) target.{m.Name}?.Clear();");
                            w.WriteLine($"\t\telse {{");
                            w.WriteLine($"\t\t\twhile (target.{m.Name}.Count > {m.Name}.Count) target.{m.Name}.RemoveAt(target.{m.Name}.Count - 1);");
                            w.WriteLine($"\t\t\tvar n = target.{m.Name}.Count;");
                            w.WriteLine($"\t\t\tfor (var i = n; i < {m.Name}.Count; i++) target.{m.Name}.Insert(i, {m.Name}[i].Create{etype.Name}());");
                            w.WriteLine($"\t\t\tfor (var i = 0; i < n; i++) {m.Name}[i].Apply(target.{m.Name}[i]);");
                            w.WriteLine($"\t\t}}");
                        }
                        else
                        {
                            if (bindings.FindType(bt.FullName) is TypeBinding b)
                            {
                                if (bt.IsValueType)
                                {
                                    w.WriteLine($"\t\ttarget.{m.Name} = {m.Name}.Create{bt.Name}();");
                                }
                                else
                                {
                                    w.WriteLine($"\t\tif ({m.Name} != null) {{");
                                    w.WriteLine($"\t\t\tif (target.{m.Name} is {bt.FullName} {m.LowerName}) {m.Name}.Apply({m.LowerName});");
                                    w.WriteLine($"\t\t\telse target.{m.Name} = {m.Name}.Create{bt.Name}();");
                                    w.WriteLine($"\t\t}} else target.{m.Name} = null;");
                                }
                            }
                            else
                            {
                                w.WriteLine($"\t\ttarget.{m.Name} = {m.Name};");
                            }
                        }
                    }
                    w.WriteLine("\t}");
                    foreach (var b in bh.Skip(1))
                    {
                        w.WriteLine($"\tpublic override void Apply({b.Definition.FullName} target) {{");
                        w.WriteLine($"\t\tif (target is {t.FullName} t) Apply(t);");
                        w.WriteLine($"\t\telse base.Apply(target);");
                        w.WriteLine("\t}");
                    }
                    w.WriteLine("}");
                }

                type.BoundCode = w.ToString ();
            }
            catch (Exception ex) {
                type.BoundCode = "/* " + ex + " */";
            }
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
