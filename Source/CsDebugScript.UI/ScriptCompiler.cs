﻿using CsDebugScript.Engine;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CsDebugScript
{
    internal static class ScriptCompiler
    {
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
        /// Gets the list of search folders.
        /// </summary>
        internal static List<string> SearchFolders { get; private set; }

        /// <summary>
        /// The default assembly references used by the compiler
        /// </summary>
        internal static readonly string[] DefaultAssemblyReferences = GetDefaultAssemblyReferences();

        /// <summary>
        /// The default list of using commands used by the compiler
        /// </summary>
        internal static readonly string[] DefaultUsings = new string[] { "System", "System.Linq", "CsDebugScript" };

        /// <summary>
        /// Gets the default assembly references used by the compiler.
        /// </summary>
        private static string[] GetDefaultAssemblyReferences()
        {
            dynamic justInitializationOfDynamics = new List<string>();
            List<string> assemblyReferences = new List<string>();

            assemblyReferences.Add(typeof(System.Object).Assembly.Location);
            assemblyReferences.Add(typeof(System.Linq.Enumerable).Assembly.Location);
            assemblyReferences.Add(typeof(CsDebugScript.Variable).Assembly.Location);
            assemblyReferences.Add(typeof(CsDebugScript.InteractiveScriptBase).Assembly.Location);

            // Check if Microsoft.CSharp.dll should be added to the list of referenced assemblies
            const string MicrosoftCSharpDll = "microsoft.csharp.dll";

            if (!assemblyReferences.Where(a => a.ToLowerInvariant().Contains(MicrosoftCSharpDll)).Any())
            {
                // TODO:
                var assembly = Assembly.LoadFile(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETCore\v4.5\Microsoft.CSharp.dll");
                assemblyReferences.AddRange(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.Location.ToLowerInvariant().Contains(MicrosoftCSharpDll)).Select(a => a.Location));
            }

            return assemblyReferences.ToArray();
        }

        /// <summary>
        /// Generates the code based on parameters.
        /// </summary>
        /// <param name="usings">The usings.</param>
        /// <param name="importedCode">The imported code.</param>
        /// <param name="scriptCode">The script code.</param>
        /// <param name="scriptBaseClassName">Name of the script base class.</param>
        internal static string GenerateCode(IEnumerable<string> usings, string importedCode, string scriptCode, string scriptBaseClassName = "CsDebugScript.ScriptBase")
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
            codeBuilder.AppendLine(importedCode);
            codeBuilder.Append("public void ");
            codeBuilder.Append(AutoGeneratedScriptFunctionName);
            codeBuilder.AppendLine("()");
            codeBuilder.AppendLine("{");
            codeBuilder.AppendLine(scriptCode);
            codeBuilder.AppendLine("}");
            codeBuilder.AppendLine("}");
            codeBuilder.AppendLine("}");
            return codeBuilder.ToString();
        }

        /// <summary>
        /// Extracts the metadata from user assemblies.
        /// </summary>
        /// <param name="assemblies">The assemblies.</param>
        internal static UserTypeMetadata[] ExtractMetadata(IEnumerable<Assembly> assemblies)
        {
            List<UserTypeMetadata> metadata = new List<UserTypeMetadata>();

            foreach (var assembly in assemblies)
            {
                List<Type> nextTypes = assembly.ExportedTypes.ToList();

                while (nextTypes.Count > 0)
                {
                    List<Type> types = nextTypes;

                    nextTypes = new List<Type>();
                    foreach (var type in types)
                    {
                        UserTypeMetadata[] userTypes = UserTypeMetadata.ReadFromType(type);

                        metadata.AddRange(userTypes);
                        nextTypes.AddRange(type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public));
                    }
                }
            }

            return metadata.ToArray();
        }
    }
}
