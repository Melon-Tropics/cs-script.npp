using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CSScriptIntellisense;
using ICSharpCode.NRefactory.Editor;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using ICSharpCode.NRefactory.CSharp;

//The same as UltraSharp.Reflector but based on Cecil
namespace UltraSharp.Cecil
{
    public class FileLocation
    {
        public string FileName;
        public int StartPosition;
        public int EndPosition;

        public DomRegion ToDomRegion()
        {
            if (string.IsNullOrEmpty(FileName))
                return DomRegion.Empty;

            string text = File.ReadAllText(FileName);
            var doc = new ReadOnlyDocument(text);
            var startLocation = doc.GetLocation(StartPosition);
            var endLocation = doc.GetLocation(EndPosition);
            return new DomRegion(FileName, startLocation, endLocation);
        }
    }

    public class Reflector
    {
        public class Result
        {
            public string Code;
            public int MemberPosition;
        }

        public Reflector(params string[] namespacesToHide)
        {
            usedNamespaces = namespacesToHide;
        }

        string[] usedNamespaces = new string[0];
        StringBuilder classDefinition = new StringBuilder();
        int intend = 0;

        static public string OutputDir;

        static public string DefaultTempDir
        {
            get { return Path.Combine(Path.GetTempPath(), "CSScriptNpp\\ReflctedTypes"); }
        }

        public FileLocation ReconstructToFile(IAssembly assembly, IType type, IMember member = null, string outputFile = null)
        {
            string file = outputFile;
            if (file.IsNullOrEmpty())
            {
                if (string.IsNullOrEmpty(OutputDir))
                    OutputDir = DefaultTempDir;

                file = Path.Combine(OutputDir, type.ToClassName(null).NormalizeAsPath());
            }

            var dir = Path.GetDirectoryName(file);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(file))
                File.Delete(file);

            Result result = Process(assembly, type, member);

            file += "." + result.Code.GetHashCode() + ".cs"; //simple caching mechanism for avoiding overrating the file

            if (!File.Exists(file))
                File.WriteAllText(file, result.Code);

            int nextLinePos = result.Code.IndexOf('\n', result.MemberPosition);
            if (nextLinePos == -1)
                nextLinePos = result.MemberPosition;

            return new FileLocation { FileName = file, StartPosition = result.MemberPosition, EndPosition = nextLinePos };
        }

        public Result Process(IType type, IMember member = null)
        {
            return Process(null, type, member);
        }

        IMember lookupMember;
        int lookupMemberPosition = -1;

        public Result Process(IAssembly assembly, IType type, IMember member = null)
        {
            lookupMember = member;
            lookupMemberPosition = -1;
            classDefinition.Clear();
            intend = 0;

            if (assembly != null)
                WriteLines("//",
                           "// This file has been decompiled from " + assembly.UnresolvedAssembly.Location,
                           "//");

            //WriteLine("using System;");
            //NoteUsedNamespace("System");

            if (!type.Namespace.IsNullOrEmpty())
            {
                NoteUsedNamespace(type.Namespace);
                WriteLine("namespace " + type.Namespace);
                WriteLine("{");
                intend++;
            }

            foreach (string name in type.GetDeclaringTypes())
            {
                WriteLine("public partial class " + name);
                WriteLine("{");
                intend++;
            }

            if (type.IsEnum())//all enum values are already printed by Process(type)
                ProcessEnum(type);
            else if (type.IsDelegate())
                ProcessDelegate(type);
            else
                ProcessClass(type);

            while (intend > 0)
            {
                intend--;
                WriteLine("}");
            }

            string completeCode = classDefinition.ToString()
                                                 .ResolveClassTypeParameters(classTypeParamsList)
                                                 .Trim();

            return new Result { Code = completeCode, MemberPosition = lookupMemberPosition };
        }

        public Reflector ProcessEnum(IType type)
        {
            var typedef = type as DefaultResolvedTypeDefinition;

            if (typedef == null || !type.IsEnum())
                return this;

            WriteLines(typedef.GetDocumentationLines());
            NoteLookupPosition();
            WriteLines("public enum " + type.Name,
                       "{");
            intend++;
            foreach (IField item in type.GetFields())
                ReconstructEnumValue(item);
            intend--;

            WriteLine("}");
            return this;
        }

        string GetInheritanceChain(IType type)
        {
            return type.GetInheritanceChain(usedNamespaces);
        }

        List<string> classTypeParamsList = new List<string>();

        public Reflector ProcessClass(IType type)
        {
            classTypeParamsList.Clear();
            var typedef = type as DefaultResolvedTypeDefinition;
            if (typedef.Kind != TypeKind.Class && typedef.Kind != TypeKind.Struct && typedef.Kind != TypeKind.Interface)
                return this;

            WriteLines(typedef.GetDocumentationLines());

            NoteLookupPosition();

            Write("public ");
            AppendIf(typedef.IsStatic, "static ");
            AppendIf(typedef.IsAbstract && !typedef.IsStatic && typedef.Kind != TypeKind.Interface, "abstract ");
            AppendIf(typedef.IsSealed && !typedef.IsStatic, "sealed ");

            string classDeclaration = typedef.Kind.ToString().ToLower() + " " + type.ToClassName(usedNamespaces, classTypeParamsList);
            classDeclaration = classDeclaration.ResolveTypeParameters(classTypeParamsList);

            Append(classDeclaration);
            string chain = GetInheritanceChain(type);
            AppendIf(chain != null, " : " + chain);
            CloseLine();
            WriteLine("{");

            intend++;

            foreach (IType item in type.GetNestedTypes())
                ReconstructNestedType(item);

            foreach (IField item in type.GetFields())
                ReconstructField(item);

            foreach (IProperty item in type.GetProperties())
                ReconstructProperty(item);

            foreach (IEvent item in type.GetEvents())
                ReconstructEvent(item);

            IEnumerable<IMethod> constructors = type.GetConstructors();
            if (constructors.Count() == 1)
            {
                IMethod method = constructors.First();
                if (!method.IsDefaultConstructor())
                    ReconstructMethod(method);
            }
            else
                foreach (IMethod item in constructors)
                    ReconstructMethod(item);

            foreach (IMethod item in type.GetMethods())
                ReconstructMethod(item);

            intend--;

            WriteLine("}");
            return this;
        }

        string GetTypeName(IField field)
        {
            return field.GetTypeName(usedNamespaces);
        }

        void ReconstructNestedType(IType type)
        {
            //NoteMemberPosition(type);

            //for generic types (ParameterizedType) properties as IsStatic are not implemented by Cecil
            var pType = type as ParameterizedType;
            //var typedef = type as ITypeDefinition;
            //var ttt = type.ToTypeReference();

            var typedef = type as DefaultResolvedTypeDefinition;
            if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct && type.Kind != TypeKind.Interface)
                return;

            if (typedef != null)
                WriteLines(typedef.GetDocumentationLines());

            Write("public ");
            AppendIf(typedef != null && typedef.IsStatic, "static ");
            AppendIf(typedef != null && typedef.IsAbstract && !typedef.IsStatic && typedef.Kind != TypeKind.Interface, "abstract ");
            //AppendIf(typedef.IsSealed && !typedef.IsStatic, "sealed "); //not very useful for the partial declarations

            CloseLine("partial " + type.Kind.ToString().ToLower() + " " + type.Name + " { }");
        }

        void ReconstructField(IField item)
        {
            var field = (item as DefaultResolvedField);
            if (field != null)
                WriteLines(field.GetDocumentationLines());

            NoteMemberPosition(item);

            Write("public ");
            AppendIf(field.IsAbstract && !field.IsStatic, "abstract ");
            AppendIf(field.IsStatic && !field.IsConst, "static ");
            AppendIf(field.IsConst, "const ");
            Append(GetTypeName(item) + " " + item.Name);
            if (field.ConstantValue != null)
            {
                if (GetTypeName(item) == "string")
                    Append(" = \"" + field.ConstantValue + "\"");
                else
                    Append(" = " + field.ConstantValue);
            }
            Append(";");
            CloseLine();
        }

        void NoteMemberPosition(IMember item)
        {
            if (item == lookupMember)
            {
                lookupMemberPosition = classDefinition.Length;
            }
        }

        void NoteLookupPosition()
        {
            lookupMemberPosition = classDefinition.Length;
        }

        void ReconstructMethod(IMethod item)
        {
            if (item.Name == "Finalize") //item.IsDestructor is always false
                return;

            var method = (item as DefaultResolvedMethod);

            WriteLines(method.GetDocumentationLines());

            NoteMemberPosition(item);

            Write("");
            if (!method.BelongsToInterface())
            {
                Append("public ");
                AppendIf(method.IsStatic, "static ");
                AppendIf(method.IsVirtual, "virtual ");
                AppendIf(method.IsAbstract && !method.IsStatic, "abstract ");
            }

            string methodText = BuildMethodDeclaration(item);

            Append(methodText);
            Append(" {}");
            CloseLine();
        }

        string BuildMethodDeclaration(IMethod item, string forceMethodName = null)
        {
            var methodSignature = new StringBuilder();

            var method = (item as DefaultResolvedMethod);
            var typeParamsList = new List<string>();
            //method.Parameters //does not return TypeParameter info for generic parameters
            var parameters = method.Parts.First().Parameters;

            string constraints = "";

            if (item.IsConstructor)
                methodSignature.Append(item.DeclaringType.Name);
            else
            {
                var methodNameParts = item.ToMethodName(usedNamespaces, typeParamsList, forceMethodName);
                methodSignature.Append(GetTypeName(method) + " " + methodNameParts.Item1);
                constraints = methodNameParts.Item2;
            }

            methodSignature.Append("(");
            foreach (IUnresolvedParameter param in parameters)
            {
                if (param != parameters.First())
                    methodSignature.Append(", ");
                else if (method.IsExtensionMethod)
                    methodSignature.Append("this ");

                if (param.IsRef) methodSignature.Append("ref ");
                if (param.IsOut) methodSignature.Append("out ");
                if (param.IsParams) methodSignature.Append("params ");
                methodSignature.Append(GetTypeName(param) + " " + param.Name);
                if (param.IsOptional)
                    methodSignature.Append(" = " + (param as DefaultUnresolvedParameter).DefaultValue);
            }
            methodSignature.Append(")");
            string methodText = methodSignature.ToString().ResolveTypeParameters(typeParamsList) + constraints;

            return methodText;
        }

        void ReconstructEvent(IEvent item)
        {
            var field = (item as DefaultResolvedEvent);
            if (field != null)
                WriteLines(field.GetDocumentationLines());

            NoteMemberPosition(item);

            Write("");
            if (!field.BelongsToInterface())
            {
                Append("public ");
                AppendIf(field.IsAbstract && !field.IsStatic, "abstract ");
                AppendIf(field.IsStatic, "static ");
            }
            Append("event " + GetTypeName(item) + " " + item.Name);
            Append(";");
            CloseLine();
        }

        string GetTypeName(IEvent field)
        {
            return field.GetTypeName(usedNamespaces);
        }

        void ReconstructProperty(IProperty item) //done
        {
            var property = (DefaultResolvedProperty)item;
            var uProperty = (DefaultUnresolvedProperty)item.UnresolvedMember;

            WriteLines(property.GetDocumentationLines());

            NoteMemberPosition(item);

            Write("");
            if (!property.BelongsToInterface())
            {
                Append("public ");
                AppendIf(property.IsStatic, "static ");
                AppendIf(property.IsVirtual, "virtual ");
                AppendIf(property.IsAbstract && !property.IsStatic, "abstract ");
            }
            if (property.IsIndexer)
            {
                Append(GetTypeName(property) + " " + "this[");
                foreach (IUnresolvedParameter par in uProperty.Parameters)
                    Append(GetTypeName(par) + " " + par.Name + ", ");
                TrimEnd(2);
                Append("] ");
            }
            else
            {
                Append(GetTypeName(property) + " " + item.Name + " ");
            }
            Append("{ ");
            AppendIf(property.CanGet && property.Getter.IsPublic, "get; ");
            AppendIf(property.CanSet && property.Setter.IsPublic, "set; ");
            Append("}");
            CloseLine();
        }

        string GetTypeName(DefaultResolvedProperty field)
        {
            return field.GetTypeName(usedNamespaces);
        }

        void ReconstructEnumValue(IField item) //done
        {
            var field = (item as DefaultResolvedField);
            if (field != null)
                WriteLines(field.GetDocumentationLines());

            NoteMemberPosition(item);
            WriteLine(item.Name + " = " + field.ConstantValue + ",");
        }

        public Reflector ProcessDelegate(IType type)
        {
            Tuple<string, string> info = type.ProcessTypeParameters(usedNamespaces, classTypeParamsList);
            string paramsList = info.Item1;
            string constraints = info.Item2;

            var typedef = type as DefaultResolvedTypeDefinition;
            if (typedef.Kind != TypeKind.Delegate)
                return this;

            var method = typedef.GetMethods(m => m.Name == "Invoke").FirstOrDefault();
            string signature = BuildMethodDeclaration(method, type.Name + paramsList).ResolveClassTypeParameters(classTypeParamsList);

            WriteLines(typedef.GetDocumentationLines());
            NoteLookupPosition();
            WriteLine("public " + signature + constraints + ";");

            return this;
        }

        void NoteUsedNamespace(string name)
        {
            if (!name.IsNullOrEmpty() && !usedNamespaces.Contains(name))
                usedNamespaces = usedNamespaces.Concat(new[] { name }).ToArray();
        }

        void WriteLine(string text = null)
        {
            for (int i = 0; i < intend; i++)
                classDefinition.Append("    ");
            classDefinition.AppendLine(text ?? "");
        }

        void WriteLines(params string[] lines)
        {
            foreach (string line in lines)
            {
                for (int i = 0; i < intend; i++)
                    classDefinition.Append("    ");
                classDefinition.AppendLine(line);
            }
        }

        void Write(string text)
        {
            for (int i = 0; i < intend; i++)
                classDefinition.Append("    ");
            classDefinition.Append(text);
        }

        void Append(string text)
        {
            classDefinition.Append(text);
        }

        void TrimEnd(int count)
        {
            classDefinition.Length = classDefinition.Length - count;
        }

        void AppendIf(bool condition, string text)
        {
            if (condition)
                classDefinition.Append(text);
        }

        void CloseLine(string text = "")
        {
            classDefinition.AppendLine(text);
        }

        string GetTypeName(IMember member)
        {
            return member.GetTypeName(usedNamespaces);
        }

        string GetTypeName(IUnresolvedParameter param)
        {
            return param.Type.GetTypeName(usedNamespaces);
        }

        string GetTypeName(DefaultParameter param)
        {
            return (param.Type as DefaultResolvedTypeDefinition).GetTypeName(usedNamespaces);
        }

        public static CodeMapItem[] GetMapOf(string code)
        {
            bool injected = CSScriptHelper.DecorateIfRequired(ref code);
            var map = GetMapOfImpl(code);

            if (injected)
            {
                int injectedLineOffset = CSScriptHelper.GetDecorationInfo(code).Item1;
                int injectedLineNumber = code.Substring(0, injectedLineOffset).Split('\n').Count();

                map = map.Where(i => i.Line != injectedLineNumber).ToArray();

                foreach (Reflector.CodeMapItem item in map)
                {
                    if (item.Line >= injectedLineNumber)
                        item.Line -= 1;
                }
            }
            return map;
        }



        static CodeMapItem[] GetMapOfImpl(string code)
        {
            var syntaxTree = new CSharpParser().Parse(code, "demo.cs");

            var map = new List<CodeMapItem>();

            var types = syntaxTree.Children.DeepAll(x => x.NodeType == NodeType.TypeDeclaration);

            foreach (var element in types)
            {
                var type = (TypeDeclaration)element;
                foreach (var member in element.Children)
                {
                    if (member.NodeType == NodeType.Member)
                    {
                        if (member is MethodDeclaration)
                        {
                            var method = (member as MethodDeclaration);
                            map.Add(new CodeMapItem
                            {
                                Line = method.StartLocation.Line,
                                Column = method.StartLocation.Column,
                                DisplayName = method.Name + "(" + new string(',', Math.Max(method.Parameters.Count - 1, 0)) + ")",
                                ParentDisplayName = type.Name,
                                MemberType = "Method"
                            });
                        }

                        if (member is PropertyDeclaration)// || member is FieldDeclaration) //FieldDeclaration has a bug, which manifests itself in the property Name not being populated ever
                        {
                            var property = (EntityDeclaration)member;
                            map.Add(new CodeMapItem
                            {
                                Line = property.StartLocation.Line,
                                Column = property.StartLocation.Column,
                                DisplayName = property.Name,
                                ParentDisplayName = type.Name,
                                MemberType = "Property"
                            });
                        }
                    }
                }
            }
            return map.ToArray();
        }

        public class CodeMapItem
        {
            public int Column;
            public int Line;
            public string DisplayName;
            public string ParentDisplayName;
            public string MemberType;
        }
    }


    public static class ReflectorExtensions
    {
        public static bool IgnoreDocumentationExceptions = false;

        static Dictionary<string, string> operatorsTranslation = new Dictionary<string, string>()
        {
            { "op_Equality", "==" },
            { "op_Inequality", "!=" },
            { "op_GreaterThan", ">" },
            { "op_LessThan", "<" },
            { "op_Addition", "+" },
            { "op_GreaterThanOrEqual", ">=" },
            { "op_LessThanOrEqual", "<=" },
            { "op_Division", "/" },
            { "op_Multiply", "-" },
            { "op_Subtraction", "*" },
            { "op_BitwiseAnd", "&" },
            { "op_BitwiseOr", "|" },
            { "op_LeftShift", "<<" },
            { "op_RightShift", ">>" },
            { "op_Modulus", "%" },
            { "op_Decrement", "--" },
            { "op_Increment", "++" },
            { "op_LogicalNot", "-" },
            { "op_OnesComplem", "~" }
        };

        public static string[] GetDocumentationLines(this IEntity entity)
        {
            if (entity.Documentation != null)
                return entity.Documentation.Xml.Text.XmlToPlainText(true, IgnoreDocumentationExceptions)
                                                    .GetLines()
                                                    .Select(l => "/// " + l)
                                                    .ToArray();
            else
                return EmptyStringArray;
        }

        static string[] EmptyStringArray = new string[0];

        public static string GetInheritanceChain(this IType type, string[] usedNamespaces)
        {
            string retval = null;

            //var types = type.GetAllBaseTypeDefinitions(); //does not return base types implemented in the different assembly
            var types = ((DefaultResolvedTypeDefinition)type).Parts.SelectMany(p => p.BaseTypes).ToArray();

            foreach (ITypeReference info in types)
            {
                string name = info.GetTypeName(EmptyStringArray);

                //if (name != "object")
                {
                    if (info != types.First())
                        retval += ", ";

                    retval += info.GetTypeName(usedNamespaces);
                }
            }

            return retval;
        }

        public static string GetTypeName(this ITypeReference type, string[] usedNamespaces)
        {
            string retval;

            if (type is ParameterizedTypeReference)
                retval = (type as ParameterizedTypeReference).GetTypeName(usedNamespaces); //generic type
            else if (type is ArrayTypeReference)
                retval = (type as ArrayTypeReference).GetTypeName(usedNamespaces);
            else if (type is DefaultUnresolvedTypeDefinition)
                retval = (type as DefaultUnresolvedTypeDefinition).FullName.NormaliseClrName(usedNamespaces);
            else if (type is PointerTypeReference)
                retval = (type as PointerTypeReference).GetTypeName(usedNamespaces);
            else if (type is DefaultResolvedTypeDefinition)
                retval = (type as DefaultResolvedTypeDefinition).GetTypeName(usedNamespaces);
            else if (type is ByReferenceTypeReference)
                retval = (type as ByReferenceTypeReference).ElementType.GetTypeName(usedNamespaces);
            else if (type is GetClassTypeReference)
                retval = (type as GetClassTypeReference).GetTypeName(usedNamespaces);
            else
                retval = type.ToString();

            return retval.NormaliseClrName(usedNamespaces);
        }

        static public string NormaliseClrName(this string text, string[] namespacesToHide)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            else
            {
                var retval = text.ReplaceClrAliaces();

                foreach (string ns in namespacesToHide)
                    if (retval.StartsWith(ns) && retval.IndexOf('.', ns.Length + 1) == -1)
                        return retval.Substring(ns.Length + 1);

                return retval;
            }
        }

        public static string ResolveTypeParameters(this string code, List<string> typeParamsList)
        {
            //This is NRefactory naming convention for the Generic type parameters !!0->TSource, !!1->TDestination

            for (int i = 0; i < typeParamsList.Count; i++)
                code = code.Replace("!!" + i, typeParamsList[i]);

            return code;
        }

        public static string ResolveClassTypeParameters(this string code, List<string> typeParamsList)
        {
            //This is NRefactory naming convention for the Generic type parameters !!0->TSource, !!1->TDestination

            for (int i = 0; i < typeParamsList.Count; i++)
                code = code.Replace("!" + i, typeParamsList[i]);

            return code;
        }

        public static bool IsDefaultConstructor(this IMethod item)
        {
            var method = (item as DefaultResolvedMethod);
            if (method != null)
                return (method.IsConstructor && method.Parameters.Count == 0);

            return false;
        }

        public static IEnumerable<string> GetDeclaringTypes(this IType type)
        {
            var parentTypes = new List<string>();
            IType parentType = type.DeclaringType;
            while (parentType != null)
            {
                parentTypes.Add(parentType.Name);
                parentType = parentType.DeclaringType;
            }
            parentTypes.Reverse();
            return parentTypes;
        }

        public static string ToClassName(this IType type, string[] usedNamespaces, List<string> typeParamsList = null)
        {
            var retval = type.Name;

            Tuple<string, string> result = type.ProcessTypeParameters(usedNamespaces, typeParamsList);
            retval += result.Item1;
            retval += result.Item2;

            return retval;
        }

        public static Tuple<string, string> ProcessTypeParameters(this IType type, string[] usedNamespaces, List<string> typeParamsList = null)
        {
            var typedef = type as DefaultResolvedTypeDefinition;

            if (typedef != null && typedef.TypeParameterCount > 0)
            {
                //NRefactory has EffectiveInterfaceSet implementation incomplete with NotImplementedException
                //so need to use DefaultUnresolvedTypeParameter of typedef.Parts.
                return ProcessTypeParameters(typedef.Parts.First().TypeParameters, usedNamespaces, typeParamsList);
            }
            else
                return new Tuple<string, string>("", "");
        }

        static Tuple<string, string> ProcessTypeParameters(this IEnumerable<IUnresolvedTypeParameter> parameters, string[] usedNamespaces, List<string> typeParamsList = null)
        {
            var constraints = new List<string>();

            var paramsText = "<";
            string constraintsText = null;

            foreach (DefaultUnresolvedTypeParameter param in parameters)
            {
                if (param != parameters.First())
                    paramsText += ", ";

                paramsText += param.Name;

                if (typeParamsList != null)
                    typeParamsList.Add(param.Name);

                string constraint = "";

                if (param.HasReferenceTypeConstraint)
                    constraint += " class,";
                else if (param.HasValueTypeConstraint)
                    constraint += " struct,";

                foreach (ITypeReference constrType in param.Constraints)
                {
                    var name = constrType.GetTypeName(usedNamespaces);
                    if (name != "System.Object" && name != "System.ValueType")
                        constraint += " " + constrType.GetTypeName(usedNamespaces) + ",";
                }

                if (param.HasReferenceTypeConstraint)
                    constraint += " new(),";

                if (constraint != "")
                {
                    constraint = constraint.Substring(0, constraint.Length - 1);
                    constraints.Add("where " + param.Name + ":" + constraint);
                }
            }

            paramsText += ">";
            if (constraints.Any())
                constraintsText = " " + string.Join(" ", constraints.ToArray());

            return new Tuple<string, string>(paramsText, constraintsText);
        }

        public static Tuple<string, string> ToMethodName(this IMethod member, string[] usedNamespaces, List<string> typeParamsList, string forceMethodName = null)
        {
            string name = forceMethodName ?? member.Name;
            string constrains = null;

            if (member.IsOperator && operatorsTranslation.ContainsKey(member.Name))
                name = "operator " + operatorsTranslation[member.Name];

            var method = member as DefaultResolvedMethod;

            if (method != null && method.TypeParameters.Count > 0)
            {
                Tuple<string, string> result = ProcessTypeParameters(method.Parts.First().TypeParameters, usedNamespaces, typeParamsList);
                name += result.Item1;
                constrains = result.Item2;
            }
            return new Tuple<string, string>(name, constrains);
        }

        public static string GetTypeName(this ParameterizedTypeReference obj, string[] usedNamespaces)
        {
            string retval = "";

            if (obj.GenericType is DefaultUnresolvedTypeDefinition)
                retval = (obj.GenericType as DefaultUnresolvedTypeDefinition).GetTypeName(usedNamespaces);
            else if (obj.GenericType is GetClassTypeReference)
                retval = (obj.GenericType as GetClassTypeReference).GetTypeName(usedNamespaces);
            else
                retval = obj.GenericType.ToString();

            bool isNullable = (retval == "System.Nullable" || retval == "Nullable");
            if (isNullable)
                retval = "";
            else
                retval += "<";

            foreach (ITypeReference arg in obj.TypeArguments)
            {
                if (arg != obj.TypeArguments.First())
                    retval += ", ";

                string typeName;

                if (arg is ParameterizedTypeReference)
                    typeName = (arg as ParameterizedTypeReference).GetTypeName(usedNamespaces);
                else if (arg is DefaultUnresolvedTypeDefinition)
                    typeName = (arg as DefaultUnresolvedTypeDefinition).FullName.NormaliseClrName(usedNamespaces);
                else if (arg is TypeParameterReference)
                    typeName = (arg as TypeParameterReference).ToString();
                else if (arg is GetClassTypeReference)
                    typeName = (arg as GetClassTypeReference).GetTypeName(usedNamespaces);
                else
                    typeName = arg.ToString();

                retval += typeName;
            }
            if (isNullable)
                retval += "?";
            else
                retval += ">";

            retval = retval.NormaliseClrName(usedNamespaces);

            return retval;
        }

        public static string GetTypeName(this GetClassTypeReference obj, string[] usedNamespaces)
        {
            string retval;

            if (string.IsNullOrEmpty(obj.Namespace))
                retval = obj.Name;
            else
                retval = obj.Namespace + "." + obj.Name;

            return retval.NormaliseClrName(usedNamespaces);
        }

        public static string GetTypeName(this PointerTypeReference obj, string[] usedNamespaces)
        {
            string retval = obj.ElementType.GetTypeName(usedNamespaces) + "*";

            return retval.NormaliseClrName(usedNamespaces);
        }

        public static string GetTypeName(this ArrayTypeReference obj, string[] usedNamespaces)
        {
            string dimentions = new string(',', obj.Dimensions - 1);
            string retval = obj.ElementType.GetTypeName(usedNamespaces) + "[" + dimentions + "]";

            return retval.NormaliseClrName(usedNamespaces);
        }

        public static bool BelongsToInterface(this IMember member)
        {
            return member.DeclaringType.Kind == TypeKind.Interface;
        }

        public static string GetTypeName(this IMember member, string[] usedNamespaces)
        {
            ParameterizedTypeReference paramTypeRef = member.UnresolvedMember.ReturnType as ParameterizedTypeReference;

            if (paramTypeRef != null) //generic type declaration
                return paramTypeRef.GetTypeName(usedNamespaces);
            else
                return member.ReturnType.FullName.NormaliseClrName(usedNamespaces);
        }

        public static string GetTypeName(this ITypeDefinition typeDef, string[] usedNamespaces)
        {
            var info = typeDef as DefaultResolvedTypeDefinition;
            //var paramTypeRef = typeDef as ParameterizedTypeReference;

            return info.FullName.NormaliseClrName(usedNamespaces);
        }

        public static bool IsEnum(this IType obj)
        {
            return ((ITypeDefinition)obj).EnumUnderlyingType.Name != "?";
        }

        public static bool IsDelegate(this IType obj)
        {
            return obj.Kind == TypeKind.Delegate;
        }

        public static bool IsNullOrEmpty(this string obj)
        {
            return string.IsNullOrEmpty(obj);
        }

        public static string NormalizeAsPath(this string obj)
        {
            return obj.Replace("\\", "_")
                      .Replace("/", "_")
                      .Replace(":", "_")
                      .Replace("*", "_")
                      .Replace("?", "_")
                      .Replace("\"", "_")
                      .Replace("<", "_")
                      .Replace(">", "_")
                      .Replace("|", "_");
        }
    }
}