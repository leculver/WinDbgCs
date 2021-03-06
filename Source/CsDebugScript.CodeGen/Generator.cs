﻿using CsDebugScript.CodeGen.UserTypes;
using Dia2Lib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CSharp;
using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace CsDebugScript.CodeGen
{
    /// <summary>
    /// Starting point for generating user types from PDBs.
    /// </summary>
    public static class Generator
    {
        /// <summary>
        /// Generates user types using the specified XML configuration.
        /// </summary>
        /// <param name="xmlConfig">The XML configuration.</param>
        /// <param name="logger">The logger text writer. If set to null, Console.Out will be used.</param>
        /// <param name="errorLogger">The error logger text writer. If set to null, Console.Error will be used.</param>
        public static void Generate(XmlConfig xmlConfig, TextWriter logger = null, TextWriter errorLogger = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            XmlModule[] xmlModules = xmlConfig.Modules;
            XmlType[] typeNames = xmlConfig.Types;
            XmlIncludedFile[] includedFiles = xmlConfig.IncludedFiles;
            XmlReferencedAssembly[] referencedAssemblies = xmlConfig.ReferencedAssemblies;
            UserTypeGenerationFlags generationOptions = xmlConfig.GetGenerationFlags();
            ConcurrentDictionary<string, string> generatedFiles = new ConcurrentDictionary<string, string>();
            var syntaxTrees = new ConcurrentBag<SyntaxTree>();
            string currentDirectory = Directory.GetCurrentDirectory();
            string outputDirectory = currentDirectory + "\\output\\";

            // Check logger and error logger
            if (errorLogger == null)
                errorLogger = Console.Error;
            if (logger == null)
                logger = Console.Out;

            // Create output directory
            Directory.CreateDirectory(outputDirectory);

            // Verify that included files exist
            if (!string.IsNullOrEmpty(xmlConfig.GeneratedAssemblyName))
                foreach (var file in includedFiles)
                    if (!File.Exists(file.Path))
                        throw new FileNotFoundException("", file.Path);

            // Loading modules
            ConcurrentDictionary<Module, XmlModule> modules = new ConcurrentDictionary<Module, XmlModule>();
            ConcurrentDictionary<XmlModule, Symbol[]> globalTypesPerModule = new ConcurrentDictionary<XmlModule, Symbol[]>();

            logger.Write("Loading modules...");
            Parallel.ForEach(xmlModules, (xmlModule) =>
            {
                Module module = Module.Open(xmlModule);

                modules.TryAdd(module, xmlModule);
            });

            logger.WriteLine(" {0}", sw.Elapsed);

            // Enumerating symbols
            logger.Write("Enumerating symbols...");
            Parallel.ForEach(modules, (mm) =>
            {
                XmlModule xmlModule = mm.Value;
                Module module = mm.Key;
                string moduleName = xmlModule.Name;
                string nameSpace = xmlModule.Namespace;
                List<Symbol> symbols = new List<Symbol>();

                foreach (var type in typeNames)
                {
                    Symbol[] foundSymbols = module.FindGlobalTypeWildcard(type.NameWildcard);

                    if (foundSymbols.Length == 0)
                        errorLogger.WriteLine("Symbol not found: {0}", type.Name);
                    else
                        symbols.AddRange(foundSymbols);
                }

                symbols.AddRange(module.GetAllTypes());
                globalTypesPerModule.TryAdd(xmlModule, symbols.ToArray());
            });

            List<Symbol> allSymbols = new List<Symbol>();
            Symbol[][] symbolsPerModule = globalTypesPerModule.Select(ss => ss.Value).ToArray();
            int maxSymbols = symbolsPerModule.Max(ss => ss.Length);

            for (int i = 0; i < maxSymbols; i++)
                for (int j = 0; j < symbolsPerModule.Length; j++)
                    if (i < symbolsPerModule[j].Length)
                        allSymbols.Add(symbolsPerModule[j][i]);

            logger.WriteLine(" {0}", sw.Elapsed);

#if false
            // Initialize symbol fields and base classes
            logger.Write("Initializing symbol values...");

            Parallel.ForEach(Partitioner.Create(allSymbols), (symbol) =>
            {
                var fields = symbol.Fields;
                var baseClasses = symbol.BaseClasses;
            });

            logger.WriteLine(" {0}", sw.Elapsed);
#endif

            logger.Write("Deduplicating symbols...");

            // Group duplicated symbols
            Dictionary<string, List<Symbol>> symbolsByName = new Dictionary<string, List<Symbol>>();
            Dictionary<Symbol, List<Symbol>> duplicatedSymbols = new Dictionary<Symbol, List<Symbol>>();

            foreach (var symbol in allSymbols)
            {
                List<Symbol> symbols;

                if (!symbolsByName.TryGetValue(symbol.Name, out symbols))
                    symbolsByName.Add(symbol.Name, symbols = new List<Symbol>());

                bool found = false;

                foreach (var s in symbols.ToArray())
                {
                    if (s.Size != 0 && symbol.Size != 0 && s.Size != symbol.Size)
                    {
#if DEBUG
                        logger.WriteLine("{0}!{1} ({2}) {3}!{4} ({5})", s.Module.Name, s.Name, s.Size, symbol.Module.Name, symbol.Name, symbol.Size);
#endif
                        continue;
                    }

                    if (s.Size == 0 && symbol.Size != 0)
                    {
                        List<Symbol> duplicates;

                        if (!duplicatedSymbols.TryGetValue(s, out duplicates))
                            duplicatedSymbols.Add(s, duplicates = new List<Symbol>());

                        duplicatedSymbols.Remove(s);
                        duplicates.Add(s);
                        duplicatedSymbols.Add(symbol, duplicates);
                        symbols.Remove(s);
                        symbols.Add(symbol);
                    }
                    else
                    {
                        List<Symbol> duplicates;

                        if (!duplicatedSymbols.TryGetValue(s, out duplicates))
                            duplicatedSymbols.Add(s, duplicates = new List<Symbol>());
                        duplicates.Add(symbol);
                    }

                    found = true;
                    break;
                }

                if (!found)
                    symbols.Add(symbol);
            }

            // Unlink duplicated symbols if two or more are named the same
            foreach (var symbols in symbolsByName.Values)
            {
                if (symbols.Count <= 1)
                    continue;

                foreach (var s in symbols.ToArray())
                {
                    List<Symbol> duplicates;

                    if (!duplicatedSymbols.TryGetValue(s, out duplicates))
                        continue;

                    symbols.AddRange(duplicates);
                    duplicatedSymbols.Remove(s);
                }
            }

            // Extracting deduplicated symbols
            Dictionary<string, Symbol[]> deduplicatedSymbols = new Dictionary<string, Symbol[]>();
            Dictionary<Symbol, string> symbolNamespaces = new Dictionary<Symbol, string>();

            foreach (var symbols in symbolsByName.Values)
            {
                if (symbols.Count != 1)
                    foreach (var s in symbols)
                        symbolNamespaces.Add(s, modules[s.Module].Namespace);
                else
                {
                    Symbol symbol = symbols.First();
                    List<Symbol> duplicates;

                    if (!duplicatedSymbols.TryGetValue(symbol, out duplicates))
                        duplicates = new List<Symbol>();
                    duplicates.Insert(0, symbol);
                    deduplicatedSymbols.Add(symbol.Name, duplicates.ToArray());

                    foreach (var s in duplicates)
                        symbolNamespaces.Add(s, xmlConfig.CommonTypesNamespace);
                }
            }

            var globalTypes = symbolsByName.SelectMany(s => s.Value).ToArray();

            logger.WriteLine(" {0}", sw.Elapsed);
            logger.WriteLine("  Total symbols: {0}", globalTypesPerModule.Sum(gt => gt.Value.Length));
            logger.WriteLine("  Unique symbol names: {0}", symbolsByName.Count);
            logger.WriteLine("  Dedupedlicated symbols: {0}", globalTypes.Length);

            // Initialize GlobalCache with deduplicatedSymbols
            GlobalCache.Update(deduplicatedSymbols);

            // Collecting types
            logger.Write("Collecting types...");

            var factory = new UserTypeFactory(xmlConfig.Transformations);
            List<UserType> userTypes = new List<UserType>();

            foreach (var module in modules.Keys)
                userTypes.Add(factory.AddSymbol(module.GlobalScope, new XmlType() { Name = "ModuleGlobals" }, modules[module].Namespace, generationOptions));

            ConcurrentBag<Symbol> simpleSymbols = new ConcurrentBag<Symbol>();
            Dictionary<Tuple<string, string>, List<Symbol>> templateSymbols = new Dictionary<Tuple<string, string>, List<Symbol>>();

            Parallel.ForEach(Partitioner.Create(globalTypes), (symbol) =>
            {
                string symbolName = symbol.Name;

                // TODO: Add configurable filter
                //
                if (symbolName.StartsWith("$") || symbolName.StartsWith("__vc_attributes") || symbolName.Contains("`anonymous-namespace'") || symbolName.Contains("`anonymous namespace'") || symbolName.Contains("::$") || symbolName.Contains("`"))
                {
                    return;
                }

                // Do not handle template referenced arguments 
                if (symbolName.Contains("&"))
                {
                    // TODO: Convert this to function pointer
                    return;
                }

                // TODO: For now remove all unnamed-type symbols
                string scopedClassName = symbol.Namespaces.Last();

                if (scopedClassName.StartsWith("<") || symbolName.Contains("::<"))
                {
                    return;
                }

                // Check if symbol contains template type.
                if (SymbolNameHelper.ContainsTemplateType(symbolName) && symbol.Tag == SymTagEnum.SymTagUDT)
                {
                    List<string> namespaces = symbol.Namespaces;
                    string className = namespaces.Last();
                    var symbolId = Tuple.Create(symbolNamespaces[symbol], SymbolNameHelper.CreateLookupNameForSymbol(symbol));

                    lock (templateSymbols)
                    {
                        if (templateSymbols.ContainsKey(symbolId) == false)
                            templateSymbols[symbolId] = new List<Symbol>() { symbol };
                        else
                            templateSymbols[symbolId].Add(symbol);
                    }

                    // TODO:
                    // Do not add physical types for template specialization (not now)
                    // do if types contains static fields
                    // nested in templates
                }
                else
                    simpleSymbols.Add(symbol);
            });

            logger.WriteLine(" {0}", sw.Elapsed);

            // Populate Templates
            logger.Write("Populating templates...");
            foreach (List<Symbol> symbols in templateSymbols.Values)
            {
                Symbol symbol = symbols.First();
                string symbolName = SymbolNameHelper.CreateLookupNameForSymbol(symbol);

                XmlType type = new XmlType()
                {
                    Name = symbolName
                };

                userTypes.AddRange(factory.AddSymbols(symbols, type, symbolNamespaces[symbol], generationOptions));
            }

            logger.WriteLine(" {0}", sw.Elapsed);

            // Specialized class
            logger.Write("Populating specialized classes...");
            foreach (Symbol symbol in simpleSymbols)
            {
                userTypes.Add(factory.AddSymbol(symbol, null, symbolNamespaces[symbol], generationOptions));
            }

            logger.WriteLine(" {0}", sw.Elapsed);

            // To solve template dependencies. Update specialization arguments once all the templates has been populated.
            logger.Write("Updating template arguments...");
            foreach (TemplateUserType templateUserType in userTypes.OfType<TemplateUserType>())
            {
                foreach (TemplateUserType specializedTemplateUserType in templateUserType.SpecializedTypes)
                    if (!specializedTemplateUserType.UpdateTemplateArguments(factory))
                    {
#if DEBUG
                        logger.WriteLine("Template user type cannot be updated: {0}", specializedTemplateUserType.Symbol.Name);
#endif
                    }
            }

            logger.WriteLine(" {0}", sw.Elapsed);

            // Post processing user types (filling DeclaredInType)
            logger.Write("Post processing user types...");
            var namespaceTypes = factory.ProcessTypes(userTypes, symbolNamespaces).ToArray();
            userTypes.AddRange(namespaceTypes);

            logger.WriteLine(" {0}", sw.Elapsed);

            // Code generation and saving it to disk
            logger.Write("Saving code to disk...");

            if (!generationOptions.HasFlag(UserTypeGenerationFlags.SingleFileExport))
            {
                // Generate Code
                Parallel.ForEach(userTypes,
                    (symbolEntry) =>
                    {
                        Tuple<string, string> result = GenerateCode(symbolEntry, factory, outputDirectory, errorLogger, generationOptions, generatedFiles);
                        string text = result.Item1;
                        string filename = result.Item2;

                        if (xmlConfig.GenerateAssemblyWithRoslyn && !string.IsNullOrEmpty(xmlConfig.GeneratedAssemblyName) && !string.IsNullOrEmpty(text))
                            lock (syntaxTrees)
                            {
                                syntaxTrees.Add(CSharpSyntaxTree.ParseText(text, path: filename, encoding: System.Text.UTF8Encoding.Default));
                            }
                    });
            }
            else
            {
                string filename = string.Format(@"{0}\everything.exported.cs", outputDirectory);
                HashSet<string> usings = new HashSet<string>();
                foreach (var symbolEntry in userTypes)
                    foreach (var u in symbolEntry.Usings)
                        usings.Add(u);

                generatedFiles.TryAdd(filename.ToLowerInvariant(), filename);
                using (StringWriter stringOutput = new StringWriter())
                using (TextWriter masterOutput = !xmlConfig.DontSaveGeneratedCodeFiles ? new StreamWriter(filename, false /* append */, System.Text.Encoding.UTF8, 16 * 1024 * 1024) : TextWriter.Null)
                {
                    foreach (var u in usings.OrderBy(s => s))
                    {
                        masterOutput.WriteLine("using {0};", u);
                        if (xmlConfig.GenerateAssemblyWithRoslyn && !string.IsNullOrEmpty(xmlConfig.GeneratedAssemblyName))
                            stringOutput.WriteLine("using {0};", u);
                    }
                    masterOutput.WriteLine();
                    if (xmlConfig.GenerateAssemblyWithRoslyn)
                        stringOutput.WriteLine();

                    ObjectPool<StringWriter> stringWriterPool = new ObjectPool<StringWriter>(() => new StringWriter());

                    Parallel.ForEach(userTypes,
                        (symbolEntry) =>
                        {
                            var output = stringWriterPool.GetObject();

                            output.GetStringBuilder().Clear();
                            GenerateCodeInSingleFile(output, symbolEntry, factory, errorLogger, generationOptions);
                            string text = output.ToString();

                            if (!string.IsNullOrEmpty(text))
                                lock (masterOutput)
                                {
                                    masterOutput.WriteLine(text);
                                    if (xmlConfig.GenerateAssemblyWithRoslyn && !string.IsNullOrEmpty(xmlConfig.GeneratedAssemblyName))
                                        stringOutput.WriteLine(text);
                                }

                            stringWriterPool.PutObject(output);
                        });

                    if (xmlConfig.GenerateAssemblyWithRoslyn && !string.IsNullOrEmpty(xmlConfig.GeneratedAssemblyName))
                        syntaxTrees.Add(CSharpSyntaxTree.ParseText(stringOutput.ToString(), path: filename, encoding: UTF8Encoding.Default));
                }
            }

            logger.WriteLine(" {0}", sw.Elapsed);

            // Compiling the code
            string binFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (xmlConfig.GenerateAssemblyWithRoslyn && !string.IsNullOrEmpty(xmlConfig.GeneratedAssemblyName))
            {
                List<MetadataReference> references = new List<MetadataReference>
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
                };

                references.AddRange(xmlConfig.ReferencedAssemblies.Select(r => MetadataReference.CreateFromFile(r.Path)));

                foreach (var includedFile in includedFiles)
                    syntaxTrees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(includedFile.Path), path: includedFile.Path, encoding: System.Text.UTF8Encoding.Default));

                CSharpCompilation compilation = CSharpCompilation.Create(
                    Path.GetFileNameWithoutExtension(xmlConfig.GeneratedAssemblyName),
                    syntaxTrees: syntaxTrees,
                    references: references,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, platform: Platform.AnyCpu));

                logger.WriteLine("Syntax trees: {0}", syntaxTrees.Count);

                string dllFilename = Path.Combine(outputDirectory, xmlConfig.GeneratedAssemblyName);
                string pdbFilename = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(dllFilename) + ".pdb");

                using (var dllStream = new FileStream(dllFilename, FileMode.Create))
                using (var pdbStream = new FileStream(pdbFilename, FileMode.Create))
                {
                    var result = compilation.Emit(dllStream, !xmlConfig.DisablePdbGeneration ? pdbStream : null);

                    if (!result.Success)
                    {
                        IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic =>
                            diagnostic.IsWarningAsError ||
                            diagnostic.Severity == DiagnosticSeverity.Error);

                        errorLogger.WriteLine("Compile errors (top 1000):");
                        foreach (var diagnostic in failures.Take(1000))
                            errorLogger.WriteLine(diagnostic);
                    }
                    else
                    {
                        logger.WriteLine("DLL size: {0}", dllStream.Position);
                        logger.WriteLine("PDB size: {0}", pdbStream.Position);
                    }
                }

                logger.WriteLine("Compiling: {0}", sw.Elapsed);
            }

            // Check whether we should generate assembly
            if (!xmlConfig.GenerateAssemblyWithRoslyn && !string.IsNullOrEmpty(xmlConfig.GeneratedAssemblyName))
            {
                var codeProvider = new CSharpCodeProvider();
                var compilerParameters = new CompilerParameters()
                {
                    IncludeDebugInformation = !xmlConfig.DisablePdbGeneration,
                    OutputAssembly = outputDirectory + xmlConfig.GeneratedAssemblyName,
                };

                compilerParameters.ReferencedAssemblies.AddRange(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => a.Location).ToArray());
                //compilerParameters.ReferencedAssemblies.AddRange(referencedAssemblies);

                const string MicrosoftCSharpDll = "Microsoft.CSharp.dll";

                if (!compilerParameters.ReferencedAssemblies.Cast<string>().Where(a => a.Contains(MicrosoftCSharpDll)).Any())
                {
                    compilerParameters.ReferencedAssemblies.Add(MicrosoftCSharpDll);
                }

                compilerParameters.ReferencedAssemblies.Add(Path.Combine(binFolder, "CsDebugScript.Engine.dll"));
                compilerParameters.ReferencedAssemblies.Add(Path.Combine(binFolder, "CsDebugScript.CommonUserTypes.dll"));

                var filesToCompile = generatedFiles.Values.Union(includedFiles.Select(f => f.Path)).ToArray();
                var compileResult = codeProvider.CompileAssemblyFromFile(compilerParameters, filesToCompile);

                if (compileResult.Errors.Count > 0)
                {
                    errorLogger.WriteLine("Compile errors (top 1000):");
                    foreach (CompilerError err in compileResult.Errors.Cast<CompilerError>().Take(1000))
                        errorLogger.WriteLine(err);
                }

                logger.WriteLine("Compiling: {0}", sw.Elapsed);
            }

            // Generating props file
            if (!string.IsNullOrEmpty(xmlConfig.GeneratedPropsFileName))
            {
                using (TextWriter output = new StreamWriter(outputDirectory + xmlConfig.GeneratedPropsFileName, false /* append */, System.Text.Encoding.UTF8, 16 * 1024 * 1024))
                {
                    output.WriteLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
                    output.WriteLine(@"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">");
                    output.WriteLine(@"  <ItemGroup>");
                    foreach (var file in generatedFiles.Values)
                        output.WriteLine(@"    <Compile Include=""{0}"" />", file);
                    output.WriteLine(@" </ItemGroup>");
                    output.WriteLine(@"</Project>");
                }
            }

            logger.WriteLine("Total time: {0}", sw.Elapsed);
        }

        /// <summary>
        /// Generates the code for user type and creates a file for it.
        /// </summary>
        /// <param name="userType">The user type.</param>
        /// <param name="factory">The user type factory.</param>
        /// <param name="outputDirectory">The output directory where code file will be stored.</param>
        /// <param name="errorOutput">The error output.</param>
        /// <param name="generationFlags">The user type generation flags.</param>
        /// <param name="generatedFiles">The list of already generated files.</param>
        /// <returns>Tuple of generated code and filename</returns>
        private static Tuple<string, string> GenerateCode(UserType userType, UserTypeFactory factory, string outputDirectory, TextWriter errorOutput, UserTypeGenerationFlags generationFlags, ConcurrentDictionary<string, string> generatedFiles)
        {
            Symbol symbol = userType.Symbol;

            if (symbol != null && symbol.Tag == SymTagEnum.SymTagBaseType)
            {
                // ignore Base (Primitive) types.
                return Tuple.Create("", "");
            }

            bool allParentsAreNamespaces = true;

            for (UserType parentType = userType.DeclaredInType; parentType != null && allParentsAreNamespaces; parentType = parentType.DeclaredInType)
            {
                allParentsAreNamespaces = parentType is NamespaceUserType;
            }

            if (userType is NamespaceUserType || !allParentsAreNamespaces)
            {
                return Tuple.Create("", "");
            }

            string classOutputDirectory = outputDirectory;
            string nameSpace = (userType.DeclaredInType as NamespaceUserType)?.FullClassName ?? userType.Namespace;

            if (!string.IsNullOrEmpty(nameSpace))
                classOutputDirectory = Path.Combine(classOutputDirectory, nameSpace.Replace(".", "\\").Replace(":", "."));
            Directory.CreateDirectory(classOutputDirectory);

            bool isEnum = userType is EnumUserType;

            string filename = string.Format(@"{0}\{1}{2}.exported.cs", classOutputDirectory, userType.ConstructorName, isEnum ? "_enum" : "");

            int index = 1;
            while (true)
            {
                if (generatedFiles.TryAdd(filename.ToLowerInvariant(), filename))
                {
                    break;
                }

                filename = string.Format(@"{0}\{1}{2}_{3}.exported.cs", classOutputDirectory, userType.ConstructorName, isEnum ? "_enum" : "", index++);
            }

            using (TextWriter output = new StreamWriter(filename))
            using (StringWriter stringOutput = new StringWriter())
            {
                userType.WriteCode(new IndentedWriter(stringOutput, generationFlags.HasFlag(UserTypeGenerationFlags.CompressedOutput)), errorOutput, factory, generationFlags);
                string text = stringOutput.ToString();
                output.WriteLine(text);
                return Tuple.Create(text, filename);
            }
        }

        /// <summary>
        /// Generates the code for user type in single file.
        /// </summary>
        /// <param name="output">The output text writer.</param>
        /// <param name="userType">The user type.</param>
        /// <param name="factory">The user type factory.</param>
        /// <param name="errorOutput">The error output.</param>
        /// <param name="generationFlags">The user type generation flags.</param>
        /// <returns><c>true</c> if code was generated for the type; otherwise <c>false</c></returns>
        private static bool GenerateCodeInSingleFile(TextWriter output, UserType userType, UserTypeFactory factory, TextWriter errorOutput, UserTypeGenerationFlags generationFlags)
        {
            Symbol symbol = userType.Symbol;

            if (symbol != null && symbol.Tag == SymTagEnum.SymTagBaseType)
            {
                // ignore Base (Primitive) types.
                return false;
            }

            if (userType.DeclaredInType != null)
            {
                return false;
            }

            userType.WriteCode(new IndentedWriter(output, generationFlags.HasFlag(UserTypeGenerationFlags.CompressedOutput)), errorOutput, factory, generationFlags);
            return true;
        }
    }

    /// <summary>
    /// Simple object pool leveraging <see cref="ConcurrentBag{T}"/>.
    /// </summary>
    /// <typeparam name="T">Type of object that will be stored in the pool.</typeparam>
    internal class ObjectPool<T>
    {
        /// <summary>
        /// The concurrent bag of objects
        /// </summary>
        private ConcurrentBag<T> objects;

        /// <summary>
        /// The object generator function
        /// </summary>
        private Func<T> objectGenerator;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectPool{T}"/> class.
        /// </summary>
        /// <param name="objectGenerator">The object generator function.</param>
        public ObjectPool(Func<T> objectGenerator)
        {
            if (objectGenerator == null)
                throw new ArgumentNullException(nameof(objectGenerator));
            objects = new ConcurrentBag<T>();
            this.objectGenerator = objectGenerator;
        }

        /// <summary>
        /// Gets the object from the pool.
        /// </summary>
        public T GetObject()
        {
            T item;

            if (objects.TryTake(out item))
                return item;
            return objectGenerator();
        }

        /// <summary>
        /// Puts the object back to the pool.
        /// </summary>
        /// <param name="item">The object.</param>
        public void PutObject(T item)
        {
            objects.Add(item);
        }
    }
}
