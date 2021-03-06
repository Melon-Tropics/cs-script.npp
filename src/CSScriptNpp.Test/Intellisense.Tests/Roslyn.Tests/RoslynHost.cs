using System;
using System.IO;
using System.Linq;
using System.Reflection;
using CSScriptIntellisense;
using Microsoft.CodeAnalysis;
using RoslynIntellisense;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Testing
{
    public class RoslynHost
    {
        public RoslynHost()
        {
            RoslynHost.Init();
        }

        static RoslynHost()
        {
            Environment.SetEnvironmentVariable("roslynintellisense_testing_dir", Environment.CurrentDirectory);
            Environment.SetEnvironmentVariable("suppress_roslyn_preloading", "true");
            Environment.SetEnvironmentVariable("roslynintellisense_path",
                                                Path.GetFullPath(@"..\..\..\..\..\CSScript.Npp\src\Roslyn.Intellisesne\Roslyn.Intellisense\bin\Debug\RoslynIntellisense.exe"));

            Config.Instance.RoslynIntellisense = true;

            Init();
        }

        public static ISymbol LoadType<T>()
        {
            var refs = new[] { typeof(T).Assembly.Location };
            var typeName = typeof(T).FullName;
            return LoadType(typeName, refs);
        }

        public static ISymbol LoadType(string typeName, params string[] refs)
        {
            var code = $"class Test {{  void Init() {{ var t = typeof({typeName}|); }} }}";
            return LoadCode(code, refs);
        }

        public static ISymbol LoadExpression(string expression, params string[] refs)
        {
            var code = $"using CSScriptIntellisense.Test; using System.Linq; using System; class Test {{  void Init() {{ {expression}|; }} }}";
            return LoadCode(code, refs);
        }

        public static ISymbol LoadCode(string code, params string[] refs)
        {
            int position = code.IndexOf("|") - 1;
            code = code.Replace("|", "");

            var doc = Autocompleter.WithWorkspace(code, refs.Concat(new[] { typeof(RoslynHost).Assembly.Location }).ToArray());

            ISymbol symbol = SymbolFinder.FindSymbolAtPositionAsync(doc, position).Result;

            return symbol;
        }

        static string roslynBinDir = null;

        static public void Init()
        {
            if (roslynBinDir == null)
            {
                roslynBinDir = Path.GetFullPath(@"..\..\..\..\..\CSScript.Npp\src\Roslyn.Intellisesne\Roslyn.Intellisense\bin\Debug");
                //AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            }
        }

        //private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        //{
        //    var path = $"{roslynBinDir}\\{args.Name.Split(',').First()}";
        //    if (File.Exists(path + ".dll"))
        //        return Assembly.LoadFrom(path + ".dll");
        //    if (File.Exists(path + ".exe"))
        //        return Assembly.LoadFrom(path + ".exe");
        //    return null;
        //}
    }
}