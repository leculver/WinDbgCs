﻿using Microsoft.CSharp;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CsScriptManaged
{
    public class ScriptCompiler : IDisposable
    {
        public class CompileException : Exception
        {
            public CompileException(CompilerError[] errors)
            {
                Errors = errors;
            }

            public CompilerError[] Errors { get; private set; }
        }

        /// <summary>
        /// The automatically generated namespace for the script
        /// </summary>
        internal const string AutoGeneratedNamespace = "AutoGeneratedNamespace";

        /// <summary>
        /// The automatically generated class name for the script
        /// </summary>
        internal const string AutoGeneratedClassName = "AutoGeneratedClassName";

        /// <summary>
        /// The automatically generated script function name
        /// </summary>
        internal const string AutoGeneratedScriptFunctionName = "ScriptFunction";

        /// <summary>
        /// The regex for code block comments
        /// </summary>
        internal const string CodeBlockComments = @"/\*(.*?)\*/";

        /// <summary>
        /// The regex for code line comments
        /// </summary>
        internal const string CodeLineComments = @"//(.*?)\r?\n";

        /// <summary>
        /// The regex for code strings
        /// </summary>
        internal const string CodeStrings = @"""((\\[^\n]|[^""\n])*)""";

        /// <summary>
        /// The regex for code verbatim strings
        /// </summary>
        internal const string CodeVerbatimStrings = @"@(""[^""]*"")+";

        /// <summary>
        /// The regex for code imports
        /// </summary>
        internal const string CodeImports = "import (([a-zA-Z][:])?([^\\/:*<>|;\"]+[\\/])*[^\\/:*<>|;\"]+);";

        /// <summary>
        /// The regex for code usings
        /// </summary>
        internal const string CodeUsings = "using ([^\";]+);";

        /// <summary>
        /// The compiled regex for removing comments
        /// </summary>
        internal static readonly Regex RegexRemoveComments = new Regex(CodeBlockComments + "|" + CodeLineComments + "|" + CodeStrings + "|" + CodeVerbatimStrings, RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// The compiled regex for extracting imports
        /// </summary>
        internal static readonly Regex RegexExtractImports = new Regex(CodeImports + "|" + CodeStrings + "|" + CodeVerbatimStrings, RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// The compiled regex for extracting usings
        /// </summary>
        internal static readonly Regex RegexExtractUsings = new Regex(CodeUsings + "|" + CodeStrings + "|" + CodeVerbatimStrings, RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// Gets the list of search folders.
        /// </summary>
        internal List<string> SearchFolders { get; } = new List<string>();

        private CSharpCodeProvider codeProvider = new CSharpCodeProvider();

        public void Dispose()
        {
            codeProvider.Dispose();
        }

        protected CompilerResults Compile(string code)
        {
            // Create compiler parameters
            var compilerParameters = new CompilerParameters()
            {
                IncludeDebugInformation = true,
            };

            compilerParameters.ReferencedAssemblies.AddRange(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => a.Location).ToArray());

            // Check if Microsoft.CSharp.dll should be added to the list of referenced assemblies
            const string MicrosoftCSharpDll = "Microsoft.CSharp.dll";

            if (!compilerParameters.ReferencedAssemblies.Cast<string>().Where(a => a.Contains(MicrosoftCSharpDll)).Any())
            {
                compilerParameters.ReferencedAssemblies.Add(MicrosoftCSharpDll);
            }

            // Compile the script
            return codeProvider.CompileAssemblyFromSource(compilerParameters, code);
        }

        /// <summary>
        /// Gets the full path of the file.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="parentPath">The parent path.</param>
        protected string GetFullPath(string path, string parentPath = "")
        {
            if (Path.IsPathRooted(path))
            {
                return path;
            }

            if (!string.IsNullOrEmpty(parentPath))
            {
                if (File.Exists(parentPath))
                {
                    parentPath = Path.GetDirectoryName(parentPath);
                }

                string newPath = Path.Combine(parentPath, path);

                if (File.Exists(newPath))
                {
                    return newPath;
                }
            }

            foreach (string folder in SearchFolders)
            {
                string newPath = Path.Combine(folder, path);

                if (File.Exists(newPath))
                {
                    return newPath;
                }
            }

            return path;
        }

        /// <summary>
        /// Loads the code from the script and imported files. It acts as precompiler.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="defaultUsings">The array of default using namespaces. If null is supplied, it will be { System, System.Linq, CsScripts }</param>
        /// <returns>Merged code of all imported script files</returns>
        protected string LoadCode(string path, string[] defaultUsings = null)
        {
            HashSet<string> loadedScripts = new HashSet<string>();
            HashSet<string> usings = new HashSet<string>(defaultUsings ?? new string[] { "System", "System.Linq", "CsScripts" });
            HashSet<string> imports = new HashSet<string>();
            StringBuilder importedCode = new StringBuilder();
            string fullPath = GetFullPath(path, Directory.GetCurrentDirectory());
            string scriptCode = ImportFile(path, usings, imports);

            loadedScripts.Add(path);
            while (imports.Count > 0)
            {
                HashSet<string> newImports = new HashSet<string>();

                foreach (string import in imports)
                {
                    if (!loadedScripts.Contains(import))
                    {
                        string code = ImportFile(import, usings, newImports);

                        importedCode.AppendLine(code);
                        loadedScripts.Add(import);
                    }
                }

                imports = newImports;
            }

            return GenerateCode(usings, importedCode.ToString(), scriptCode);
        }

        protected static string GenerateCode(IEnumerable<string> usings, string importedCode, string scriptCode, string scriptBaseClassName = "CsScripts.ScriptBase")
        {
            StringBuilder codeBuilder = new StringBuilder();

            foreach (var u in usings.OrderBy(a => a))
            {
                codeBuilder.Append("using ");
                codeBuilder.Append(u);
                codeBuilder.AppendLine(";");
            }

            codeBuilder.Append("namespace ");
            codeBuilder.AppendLine(AutoGeneratedNamespace);
            codeBuilder.AppendLine("{");
            codeBuilder.Append("public class ");
            codeBuilder.Append(AutoGeneratedClassName);
            codeBuilder.Append(" : ");
            codeBuilder.AppendLine(scriptBaseClassName);
            codeBuilder.AppendLine("{");
            codeBuilder.AppendLine(importedCode.ToString());
            codeBuilder.Append("public void ");
            codeBuilder.Append(AutoGeneratedScriptFunctionName);
            codeBuilder.AppendLine("(string[] args)");
            codeBuilder.AppendLine("{");
            codeBuilder.AppendLine(scriptCode);
            codeBuilder.AppendLine("}");
            codeBuilder.AppendLine("}");
            codeBuilder.AppendLine("}");
            return codeBuilder.ToString();
        }

        /// <summary>
        /// Imports the file.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="usings">The usings.</param>
        /// <param name="imports">The imports.</param>
        /// <returns>Code of the imported file</returns>
        protected string ImportFile(string path, ICollection<string> usings, ICollection<string> imports)
        {
            string code = File.ReadAllText(path);
            HashSet<string> localImports = new HashSet<string>();

            code = RemoveComments(code);
            code = ExtractImports(code, localImports);
            code = ExtractUsings(code, usings);
            foreach (string import in localImports)
            {
                imports.Add(GetFullPath(import, path));
            }

            return "#line 1 \"" + path + "\"\n" + code + "\n#line default\n";
        }

        /// <summary>
        /// Imports the code.
        /// </summary>
        /// <param name="code">The code.</param>
        /// <param name="usings">The usings.</param>
        /// <param name="imports">The imports.</param>
        /// <returns>Code without extracted usings, imports and comments</returns>
        protected string ImportCode(string code, ICollection<string> usings, ICollection<string> imports)
        {
            HashSet<string> localImports = new HashSet<string>();

            code = RemoveComments(code);
            code = ExtractImports(code, localImports);
            code = ExtractUsings(code, usings);
            foreach (string import in localImports)
            {
                imports.Add(GetFullPath(import));
            }

            return code;
        }

        /// <summary>
        /// Cleans the code for removal: replaces all non-newline characters with space.
        /// </summary>
        /// <param name="code">The code.</param>
        protected static string CleanCodeForRemoval(string code)
        {
            StringBuilder sb = new StringBuilder();

            foreach (char c in code)
                if (c == '\n')
                    sb.AppendLine();
                else
                    sb.Append(' ');
            return sb.ToString();
        }

        /// <summary>
        /// Removes the comments from the code.
        /// </summary>
        /// <param name="code">The code.</param>
        /// <returns>Code without comments.</returns>
        protected static string RemoveComments(string code)
        {
            return RegexRemoveComments.Replace(code,
                me =>
                {
                    if (me.Value.StartsWith("/*") || me.Value.StartsWith("//"))
                        return CleanCodeForRemoval(me.Value);
                    return me.Value;
                });
        }

        /// <summary>
        /// Extracts the imports from the code.
        /// </summary>
        /// <param name="code">The code.</param>
        /// <param name="imports">The imports.</param>
        /// <returns>Code without imports.</returns>
        protected static string ExtractImports(string code, ICollection<string> imports)
        {
            return RegexExtractImports.Replace(code,
                me =>
                {
                    if (me.Value.StartsWith("import"))
                    {
                        imports.Add(me.Groups[1].Value);
                        return CleanCodeForRemoval(me.Value);
                    }

                    return me.Value;
                });
        }

        /// <summary>
        /// Extracts the usings from the code.
        /// </summary>
        /// <param name="code">The code.</param>
        /// <param name="usings">The usings.</param>
        /// <returns>Code without usings.</returns>
        protected static string ExtractUsings(string code, ICollection<string> usings)
        {
            return RegexExtractUsings.Replace(code,
                me =>
                {
                    if (me.Value.StartsWith("using"))
                    {
                        usings.Add(me.Groups[1].Value);
                        return CleanCodeForRemoval(me.Value);
                    }

                    return me.Value;
                });
        }
    }
}