using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace Packages.Rider.Editor
{
  public interface IGenerator
  {
    bool SyncIfNeeded(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles);
    void Sync();
    bool HasSolutionBeenGenerated();
    string SolutionFile();
    string ProjectDirectory { get; }
    void GenerateAll(bool generateAll);
  }

  public interface IAssemblyNameProvider
  {
    string GetAssemblyNameFromScriptPath(string path);
    IEnumerable<Assembly> GetAllAssemblies(Func<string, bool> shouldFileBePartOfSolution);
    IEnumerable<string> GetAllAssetPaths();
    UnityEditor.PackageManager.PackageInfo FindForAssetPath(string assetPath);
    string CollectAssemblyNames(string asset);
  }

  public struct TestSettings
  {
    public bool ShouldSync;
    public Dictionary<string, string> SyncPath;
  }

  class AssemblyNameProvider : IAssemblyNameProvider
  {
    public string GetAssemblyNameFromScriptPath(string path)
    {
      return CompilationPipeline.GetAssemblyNameFromScriptPath(path);
    }

    public IEnumerable<Assembly> GetAllAssemblies(Func<string, bool> shouldFileBePartOfSolution)
    {
      return CompilationPipeline.GetAssemblies()
        .Where(i => 0 < i.sourceFiles.Length && i.sourceFiles.Any(shouldFileBePartOfSolution));
    }

    public IEnumerable<string> GetAllAssetPaths()
    {
      return AssetDatabase.GetAllAssetPaths();
    }

    public UnityEditor.PackageManager.PackageInfo FindForAssetPath(string assetPath)
    {
      return UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
    }

    public string CollectAssemblyNames(string asset)
    {
      return CompilationPipeline.GetAssemblyNameFromScriptPath(asset + ".cs");
    }
  }

  public class ProjectGeneration : IGenerator
  {
    enum ScriptingLanguage
    {
      None,
      CSharp
    }

    public static readonly string MSBuildNamespaceUri = "http://schemas.microsoft.com/developer/msbuild/2003";

    const string k_WindowsNewline = "\r\n";

    /// <summary>
    /// Map source extensions to ScriptingLanguages
    /// </summary>
    static readonly Dictionary<string, ScriptingLanguage> k_BuiltinSupportedExtensions =
      new Dictionary<string, ScriptingLanguage>
      {
        {"cs", ScriptingLanguage.CSharp},
        {"uxml", ScriptingLanguage.None},
        {"uss", ScriptingLanguage.None},
        {"shader", ScriptingLanguage.None},
        {"compute", ScriptingLanguage.None},
        {"cginc", ScriptingLanguage.None},
        {"hlsl", ScriptingLanguage.None},
        {"glslinc", ScriptingLanguage.None}
      };

    string m_SolutionProjectEntryTemplate = string.Join("\r\n",
      @"Project(""{{{0}}}"") = ""{1}"", ""{2}"", ""{{{3}}}""",
      @"EndProject").Replace("    ", "\t");

    string m_SolutionProjectConfigurationTemplate = string.Join("\r\n",
      @"        {{{0}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
      @"        {{{0}}}.Debug|Any CPU.Build.0 = Debug|Any CPU",
      @"        {{{0}}}.Release|Any CPU.ActiveCfg = Release|Any CPU",
      @"        {{{0}}}.Release|Any CPU.Build.0 = Release|Any CPU").Replace("    ", "\t");

    static readonly string[] k_ReimportSyncExtensions = {".dll", ".asmdef"};

    /// <summary>
    /// Map ScriptingLanguages to project extensions
    /// </summary>
    /*static readonly Dictionary<ScriptingLanguage, string> k_ProjectExtensions = new Dictionary<ScriptingLanguage, string>
    {
        { ScriptingLanguage.CSharp, ".csproj" },
        { ScriptingLanguage.None, ".csproj" },
    };*/
    static readonly Regex k_ScriptReferenceExpression = new Regex(
      @"^Library.ScriptAssemblies.(?<dllname>(?<project>.*)\.dll$)",
      RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    string[] m_ProjectSupportedExtensions = new string[0];
    bool m_ShouldGenerateAll;

    public string ProjectDirectory { get; }

    public void GenerateAll(bool generateAll)
    {
      m_ShouldGenerateAll = generateAll;
    }

    public TestSettings Settings { get; set; }
    readonly string m_ProjectName;
    readonly IAssemblyNameProvider m_AssemblyNameProvider;

    const string k_ToolsVersion = "4.0";
    const string k_ProductVersion = "10.0.20506";
    const string k_BaseDirectory = ".";
    const string k_TargetFrameworkVersion = "v4.7.1";
    const string k_TargetLanguageVersion = "latest";

    public ProjectGeneration() : this(Directory.GetParent(Application.dataPath).FullName, new AssemblyNameProvider())
    {
    }

    public ProjectGeneration(string tempDirectory) : this(tempDirectory, new AssemblyNameProvider())
    {
    }

    public ProjectGeneration(string tempDirectory, IAssemblyNameProvider assemblyNameProvider)
    {
      Settings = new TestSettings {ShouldSync = true};
      ProjectDirectory = tempDirectory.Replace('\\', '/');
      m_ProjectName = Path.GetFileName(ProjectDirectory);
      m_AssemblyNameProvider = assemblyNameProvider;
    }

    /// <summary>
    /// Syncs the scripting solution if any affected files are relevant.
    /// </summary>
    /// <returns>
    /// Whether the solution was synced.
    /// </returns>
    /// <param name='affectedFiles'>
    /// A set of files whose status has changed
    /// </param>
    /// <param name="reimportedFiles">
    /// A set of files that got reimported
    /// </param>
    public bool SyncIfNeeded(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles)
    {
      Profiler.BeginSample("SolutionSynchronizerSyncIfNeeded");
      SetupProjectSupportedExtensions();

      // Don't sync if we haven't synced before
      var reimportedList = reimportedFiles.ToList();
      var affectedList = affectedFiles.ToList();
      if (HasSolutionBeenGenerated() && HasFilesBeenModified(affectedList, reimportedList))
      {
        var assemblies = m_AssemblyNameProvider.GetAllAssemblies(ShouldFileBePartOfSolution).ToList();
        var allProjectIslands = RelevantIslandsForMode(assemblies).ToList();
        var allAssetProjectParts = GenerateAllAssetProjectParts();
        //Sync();
        var affected = affectedList.Select(CollectAssemblyNames).Select(name => name.Substring(0, name.Length - ".dll".Length)).ToList();
        var reimported = reimportedList.Select(CollectAssemblyNames).Select(name => name.Substring(0, name.Length - ".dll".Length)).ToList();
        var affectedAndReimported = new HashSet<string>(affected.Concat(reimported));
        var necessary = assemblies.Where(assembly =>
          affectedAndReimported.Contains(Utility.FileNameWithoutExtension(assembly.outputPath)));
        foreach (var island in necessary)
        {
          SyncProject(island, allAssetProjectParts, ParseResponseFileData(island), allProjectIslands);
        }
        Profiler.EndSample();
        return true;
      }

      Profiler.EndSample();
      return false;
    }

    string CollectAssemblyNames(string asset)
    {
      return m_AssemblyNameProvider.CollectAssemblyNames(asset);
    }

    bool HasFilesBeenModified(List<string> affectedFiles, List<string> reimportedFiles)
    {
      return affectedFiles.Any(ShouldFileBePartOfSolution) || reimportedFiles.Any(ShouldSyncOnReimportedAsset);
    }

    static bool ShouldSyncOnReimportedAsset(string asset)
    {
      return k_ReimportSyncExtensions.Contains(new FileInfo(asset).Extension);
    }

    public void Sync()
    {
      SetupProjectSupportedExtensions();
      bool externalCodeAlreadyGeneratedProjects = OnPreGeneratingCSProjectFiles();

      if (!externalCodeAlreadyGeneratedProjects)
      {
        GenerateAndWriteSolutionAndProjects();
      }

      OnGeneratedCSProjectFiles();
    }

    public bool HasSolutionBeenGenerated()
    {
      return File.Exists(SolutionFile());
    }

    void SetupProjectSupportedExtensions()
    {
      m_ProjectSupportedExtensions = EditorSettings.projectGenerationUserExtensions;
    }

    bool ShouldFileBePartOfSolution(string file)
    {
      string extension = Path.GetExtension(file);

      // Exclude files coming from packages except if they are internalized.
      if (!m_ShouldGenerateAll && IsInternalizedPackagePath(file))
      {
        return false;
      }

      // Dll's are not scripts but still need to be included..
      if (extension == ".dll")
        return true;

      if (file.ToLowerInvariant().EndsWith(".asmdef", StringComparison.Ordinal))
        return true;

      return IsSupportedExtension(extension);
    }

    bool IsSupportedExtension(string extension)
    {
      extension = extension.TrimStart('.');
      if (k_BuiltinSupportedExtensions.ContainsKey(extension))
        return true;
      if (m_ProjectSupportedExtensions.Contains(extension))
        return true;
      return false;
    }

    static ScriptingLanguage ScriptingLanguageFor(Assembly island)
    {
      return ScriptingLanguageFor(GetExtensionOfSourceFiles(island.sourceFiles));
    }

    static string GetExtensionOfSourceFiles(string[] files)
    {
      return files.Length > 0 ? GetExtensionOfSourceFile(files[0]) : "NA";
    }

    static string GetExtensionOfSourceFile(string file)
    {
      var ext = Path.GetExtension(file).ToLowerInvariant();
      ext = ext.Substring(1); //strip dot
      return ext;
    }

    static ScriptingLanguage ScriptingLanguageFor(string extension)
    {
      return k_BuiltinSupportedExtensions.TryGetValue(extension.TrimStart('.'), out var result)
        ? result
        : ScriptingLanguage.None;
    }

    public void GenerateAndWriteSolutionAndProjects()
    {
      // Only synchronize islands that have associated source files and ones that we actually want in the project.
      // This also filters out DLLs coming from .asmdef files in packages.
      var assemblies = m_AssemblyNameProvider.GetAllAssemblies(ShouldFileBePartOfSolution);

      var allAssetProjectParts = GenerateAllAssetProjectParts();

      var monoIslands = assemblies.ToList();

      SyncSolution(monoIslands);
      var allProjectIslands = RelevantIslandsForMode(monoIslands).ToList();
      foreach (Assembly assembly in allProjectIslands)
      {
        var responseFileData = ParseResponseFileData(assembly);
        SyncProject(assembly, allAssetProjectParts, responseFileData, allProjectIslands);
      }
    }

    IEnumerable<ResponseFileData> ParseResponseFileData(Assembly assembly)
    {
      var systemReferenceDirectories =
        CompilationPipeline.GetSystemAssemblyDirectories(assembly.compilerOptions.ApiCompatibilityLevel);

      Dictionary<string, ResponseFileData> responseFilesData = assembly.compilerOptions.ResponseFiles.ToDictionary(
        x => x, x => CompilationPipeline.ParseResponseFile(
          Path.Combine(ProjectDirectory, x),
          ProjectDirectory,
          systemReferenceDirectories
        ));

      Dictionary<string, ResponseFileData> responseFilesWithErrors = responseFilesData.Where(x => x.Value.Errors.Any())
        .ToDictionary(x => x.Key, x => x.Value);

      if (responseFilesWithErrors.Any())
      {
        foreach (var error in responseFilesWithErrors)
        foreach (var valueError in error.Value.Errors)
        {
          Debug.LogError($"{error.Key} Parse Error : {valueError}");
        }
      }

      return responseFilesData.Select(x => x.Value);
    }

    Dictionary<string, string> GenerateAllAssetProjectParts()
    {
      Dictionary<string, StringBuilder> stringBuilders = new Dictionary<string, StringBuilder>();

      foreach (string asset in m_AssemblyNameProvider.GetAllAssetPaths())
      {
        // Exclude files coming from packages except if they are internalized.
        if (!m_ShouldGenerateAll && IsInternalizedPackagePath(asset))
        {
          continue;
        }

        string extension = Path.GetExtension(asset);
        if (IsSupportedExtension(extension) && ScriptingLanguage.None == ScriptingLanguageFor(extension))
        {
          // Find assembly the asset belongs to by adding script extension and using compilation pipeline.
          var assemblyName = m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset + ".cs");

          if (string.IsNullOrEmpty(assemblyName))
          {
            continue;
          }

          assemblyName = Utility.FileNameWithoutExtension(assemblyName);

          if (!stringBuilders.TryGetValue(assemblyName, out var projectBuilder))
          {
            projectBuilder = new StringBuilder();
            stringBuilders[assemblyName] = projectBuilder;
          }

          projectBuilder.Append("     <None Include=\"").Append(EscapedRelativePathFor(asset)).Append("\" />")
            .Append(k_WindowsNewline);
        }
      }

      var result = new Dictionary<string, string>();

      foreach (var entry in stringBuilders)
        result[entry.Key] = entry.Value.ToString();

      return result;
    }

    bool IsInternalizedPackagePath(string file)
    {
      if (string.IsNullOrWhiteSpace(file))
      {
        return false;
      }

      var packageInfo = m_AssemblyNameProvider.FindForAssetPath(file);
      if (packageInfo == null)
      {
        return false;
      }

      var packageSource = packageInfo.source;
      return packageSource != PackageSource.Embedded && packageSource != PackageSource.Local;
    }

    void SyncProject(
      Assembly island,
      Dictionary<string, string> allAssetsProjectParts,
      IEnumerable<ResponseFileData> responseFilesData,
      List<Assembly> allProjectIslands)
    {
      SyncProjectFileIfNotChanged(ProjectFile(island),
        ProjectText(island, allAssetsProjectParts, responseFilesData, allProjectIslands));
    }

    void SyncProjectFileIfNotChanged(string path, string newContents)
    {
      if (Path.GetExtension(path) == ".csproj")
      {
        newContents = OnGeneratedCSProject(path, newContents);
      }

      SyncFileIfNotChanged(path, newContents);
    }

    void SyncSolutionFileIfNotChanged(string path, string newContents)
    {
      newContents = OnGeneratedSlnSolution(path, newContents);

      SyncFileIfNotChanged(path, newContents);
    }

    static List<Type> SafeGetTypes(System.Reflection.Assembly a)
    {
      var ret = new List<Type>();

      try
      {
        ret = a.GetTypes().ToList();
      }
      catch (System.Reflection.ReflectionTypeLoadException rtl)
      {
        ret = rtl.Types.ToList();
      }
      catch (Exception)
      {
        return new List<Type>();
      }

      return ret.Where(r => r != null).ToList();
    }

    static void OnGeneratedCSProjectFiles()
    {
      var types = TypeCache.GetTypesDerivedFrom<AssetPostprocessor>();
      var args = new object[0];
      foreach (var type in types)
      {
        var method = type.GetMethod("OnGeneratedCSProjectFiles",
          System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
          System.Reflection.BindingFlags.Static);
        if (method == null)
        {
          continue;
        }

        method.Invoke(null, args);
      }
    }

    static bool OnPreGeneratingCSProjectFiles()
    {
      var types = TypeCache.GetTypesDerivedFrom<AssetPostprocessor>();
      bool result = false;
      foreach (var type in types)
      {
        var args = new object[0];
        var method = type.GetMethod("OnPreGeneratingCSProjectFiles",
          System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
          System.Reflection.BindingFlags.Static);
        if (method == null)
        {
          continue;
        }

        var returnValue = method.Invoke(null, args);
        if (method.ReturnType == typeof(bool))
        {
          result |= (bool) returnValue;
        }
      }

      return result;
    }

    static string OnGeneratedCSProject(string path, string content)
    {
      var types = TypeCache.GetTypesDerivedFrom<AssetPostprocessor>();
      foreach (var type in types)
      {
        var args = new[] {path, content};
        var method = type.GetMethod("OnGeneratedCSProject",
          System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
          System.Reflection.BindingFlags.Static);
        if (method == null)
        {
          continue;
        }

        var returnValue = method.Invoke(null, args);
        if (method.ReturnType == typeof(string))
        {
          content = (string) returnValue;
        }
      }

      return content;
    }

    static string OnGeneratedSlnSolution(string path, string content)
    {
      var types = TypeCache.GetTypesDerivedFrom<AssetPostprocessor>();
      foreach (var type in types)
      {
        var args = new[] {path, content};
        var method = type.GetMethod("OnGeneratedSlnSolution",
          System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
          System.Reflection.BindingFlags.Static);
        if (method == null)
        {
          continue;
        }

        var returnValue = method.Invoke(null, args);
        if (method.ReturnType == typeof(string))
        {
          content = (string) returnValue;
        }
      }

      return content;
    }

    void SyncFileIfNotChanged(string filename, string newContents)
    {
      if (File.Exists(filename) &&
          newContents == File.ReadAllText(filename))
      {
        return;
      }

      if (Settings.ShouldSync)
      {
        File.WriteAllText(filename, newContents, Encoding.UTF8);
      }
      else
      {
        var utf8 = Encoding.UTF8;
        byte[] utfBytes = utf8.GetBytes(newContents);
        Settings.SyncPath[filename] = utf8.GetString(utfBytes, 0, utfBytes.Length);
      }
    }

    string ProjectText(Assembly assembly,
      Dictionary<string, string> allAssetsProjectParts,
      IEnumerable<ResponseFileData> responseFilesData,
      List<Assembly> allProjectIslands)
    {
      var projectBuilder = ProjectHeader(assembly, responseFilesData);
      var references = new List<string>();
      var projectReferences = new List<Match>();

      foreach (string file in assembly.sourceFiles)
      {
        if (!ShouldFileBePartOfSolution(file))
          continue;

        var extension = Path.GetExtension(file).ToLowerInvariant();
        var fullFile = EscapedRelativePathFor(file);
        if (".dll" != extension)
        {
          projectBuilder.Append("     <Compile Include=\"").Append(fullFile).Append("\" />").Append(k_WindowsNewline);
        }
        else
        {
          references.Add(fullFile);
        }
      }

      var assemblyName = Utility.FileNameWithoutExtension(assembly.outputPath);

      // Append additional non-script files that should be included in project generation.
      if (allAssetsProjectParts.TryGetValue(assemblyName, out var additionalAssetsForProject))
        projectBuilder.Append(additionalAssetsForProject);

      var islandRefs = references.Union(assembly.allReferences);

      foreach (string reference in islandRefs)
      {
        if (reference.EndsWith("/UnityEditor.dll", StringComparison.Ordinal)
            || reference.EndsWith("/UnityEngine.dll", StringComparison.Ordinal)
            || reference.EndsWith("\\UnityEditor.dll", StringComparison.Ordinal)
            || reference.EndsWith("\\UnityEngine.dll", StringComparison.Ordinal))
          continue;

        var match = k_ScriptReferenceExpression.Match(reference);
        if (match.Success)
        {
          // assume csharp language
          // Add a reference to a project except if it's a reference to a script assembly
          // that we are not generating a project for. This will be the case for assemblies
          // coming from .assembly.json files in non-internalized packages.
          var dllName = match.Groups["dllname"].Value;
          if (allProjectIslands.Any(i => Path.GetFileName(i.outputPath) == dllName))
          {
            projectReferences.Add(match);
            continue;
          }
        }

        string fullReference = Path.IsPathRooted(reference) ? reference : Path.Combine(ProjectDirectory, reference);

        AppendReference(fullReference, projectBuilder);
      }

      var responseRefs = responseFilesData.SelectMany(x => x.FullPathReferences.Select(r => r));
      foreach (var reference in responseRefs)
      {
        AppendReference(reference, projectBuilder);
      }

      if (0 < projectReferences.Count)
      {
        projectBuilder.AppendLine("  </ItemGroup>");
        projectBuilder.AppendLine("  <ItemGroup>");
        foreach (Match reference in projectReferences)
        {
          var referencedProject = reference.Groups["project"].Value;

          projectBuilder.Append("    <ProjectReference Include=\"").Append(referencedProject)
            .Append(GetProjectExtension()).Append("\">").Append(k_WindowsNewline);
          projectBuilder.Append("      <Project>{")
            .Append(ProjectGuid(Path.Combine("Temp", reference.Groups["project"].Value + ".dll"))).Append("}</Project>")
            .Append(k_WindowsNewline);
          projectBuilder.Append("      <Name>").Append(referencedProject).Append("</Name>").Append(k_WindowsNewline);
          projectBuilder.AppendLine("    </ProjectReference>");
        }
      }

      projectBuilder.Append(ProjectFooter());
      return projectBuilder.ToString();
    }

    static void AppendReference(string fullReference, StringBuilder projectBuilder)
    {
      //replace \ with / and \\ with /
      var escapedFullPath = SecurityElement.Escape(fullReference).Replace("\\\\", "/").Replace("\\", "/");
      projectBuilder.Append(" <Reference Include=\"").Append(Utility.FileNameWithoutExtension(escapedFullPath))
        .Append("\">").Append(k_WindowsNewline);
      projectBuilder.Append(" <HintPath>").Append(escapedFullPath).Append("</HintPath>").Append(k_WindowsNewline);
      projectBuilder.Append(" </Reference>").Append(k_WindowsNewline);
    }

    public string ProjectFile(Assembly assembly)
    {
      return Path.Combine(ProjectDirectory, $"{Utility.FileNameWithoutExtension(assembly.outputPath)}.csproj");
    }

    public string SolutionFile()
    {
      return Path.Combine(ProjectDirectory, $"{m_ProjectName}.sln");
    }

    StringBuilder ProjectHeader(
      Assembly island,
      IEnumerable<ResponseFileData> responseFilesData
    )
    {
      var responseFileDatas = responseFilesData as ResponseFileData[] ?? responseFilesData.ToArray();
      var defineConstants = string.Join(";",
        new[] { "DEBUG", "TRACE" }, EditorUserBuildSettings.activeScriptCompilationDefines, island.defines,
        responseFileDatas.SelectMany(x => x.Defines).Distinct());
      var builder = new StringBuilder(40000);
      builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>").Append(k_WindowsNewline);
      builder.Append("<Project ToolsVersion=\"").Append(k_ToolsVersion).Append("\" DefaultTargets=\"Build\" xmlns=\"")
        .Append(MSBuildNamespaceUri).Append("\">").Append(k_WindowsNewline);
      builder.Append("  <PropertyGroup>").Append(k_WindowsNewline);
      builder.Append("    <LangVersion>").Append(k_TargetLanguageVersion).Append("</LangVersion>").Append(k_WindowsNewline);
      builder.Append("  </PropertyGroup>").Append(k_WindowsNewline);
      builder.Append("  <PropertyGroup>").Append(k_WindowsNewline);
      builder.Append("    <Configuration Condition=\" '$(Configuration)' == '' \">Debug</Configuration>").Append(k_WindowsNewline);
      builder.Append("    <Platform Condition=\" '$(Platform)' == '' \">AnyCPU</Platform>").Append(k_WindowsNewline);
      builder.Append("    <ProductVersion>").Append(k_ProductVersion).Append("</ProductVersion>").Append(k_WindowsNewline);
      builder.Append("    <SchemaVersion>2.0</SchemaVersion>").Append(k_WindowsNewline);
      builder.Append("    <RootNamespace>").Append(EditorSettings.projectGenerationRootNamespace).Append("</RootNamespace>").Append(k_WindowsNewline);
      builder.Append("    <ProjectGuid>{").Append(ProjectGuid(island.outputPath)).Append("}</ProjectGuid>").Append(k_WindowsNewline);
      builder.Append("    <OutputType>Library</OutputType>").Append(k_WindowsNewline);
      builder.Append("    <AppDesignerFolder>Properties</AppDesignerFolder>").Append(k_WindowsNewline);
      builder.Append("    <AssemblyName>").Append(Utility.FileNameWithoutExtension(island.outputPath)).Append("</AssemblyName>").Append(k_WindowsNewline);
      builder.Append("    <TargetFrameworkVersion>").Append(k_TargetFrameworkVersion).Append("</TargetFrameworkVersion>").Append(k_WindowsNewline);
      builder.Append("    <FileAlignment>512</FileAlignment>").Append(k_WindowsNewline);
      builder.Append("    <BaseDirectory>").Append(k_BaseDirectory).Append("</BaseDirectory>").Append(k_WindowsNewline);
      builder.Append("  </PropertyGroup>").Append(k_WindowsNewline);
      builder.Append("  <PropertyGroup Condition=\" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' \">").Append(k_WindowsNewline);
      builder.Append("    <DebugSymbols>true</DebugSymbols>").Append(k_WindowsNewline);
      builder.Append("    <DebugType>full</DebugType>").Append(k_WindowsNewline);
      builder.Append("    <Optimize>false</Optimize>").Append(k_WindowsNewline);
      builder.Append("    <OutputPath>Temp\\bin\\Debug\\</OutputPath>").Append(k_WindowsNewline);
      builder.Append("    <DefineConstants>").Append(defineConstants).Append("</DefineConstants>").Append(k_WindowsNewline);
      builder.Append("    <ErrorReport>prompt</ErrorReport>").Append(k_WindowsNewline);
      builder.Append("    <WarningLevel>4</WarningLevel>").Append(k_WindowsNewline);
      builder.Append("    <NoWarn>0169</NoWarn>").Append(k_WindowsNewline);
      builder.Append("    <AllowUnsafeBlocks>").Append(island.compilerOptions.AllowUnsafeCode | responseFileDatas.Any(x => x.Unsafe)).Append("</AllowUnsafeBlocks>").Append(k_WindowsNewline);
      builder.Append("  </PropertyGroup>").Append(k_WindowsNewline);
      builder.Append("  <PropertyGroup Condition=\" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' \">").Append(k_WindowsNewline);
      builder.Append("    <DebugType>pdbonly</DebugType>").Append(k_WindowsNewline);
      builder.Append("    <Optimize>true</Optimize>").Append(k_WindowsNewline);
      builder.Append("    <OutputPath>Temp\\bin\\Release\\</OutputPath>").Append(k_WindowsNewline);
      builder.Append("    <ErrorReport>prompt</ErrorReport>").Append(k_WindowsNewline);
      builder.Append("    <WarningLevel>4</WarningLevel>").Append(k_WindowsNewline);
      builder.Append("    <NoWarn>0169</NoWarn>").Append(k_WindowsNewline);
      builder.Append("    <AllowUnsafeBlocks>").Append(island.compilerOptions.AllowUnsafeCode | responseFileDatas.Any(x => x.Unsafe)).Append("</AllowUnsafeBlocks>").Append(k_WindowsNewline);
      builder.Append("  </PropertyGroup>").Append(k_WindowsNewline);
      builder.Append("  <PropertyGroup>").Append(k_WindowsNewline);
      builder.Append("    <NoConfig>true</NoConfig>").Append(k_WindowsNewline);
      builder.Append("    <NoStdLib>true</NoStdLib>").Append(k_WindowsNewline);
      builder.Append("    <AddAdditionalExplicitAssemblyReferences>false</AddAdditionalExplicitAssemblyReferences>").Append(k_WindowsNewline);
      builder.Append("    <ImplicitlyExpandNETStandardFacades>false</ImplicitlyExpandNETStandardFacades>").Append(k_WindowsNewline);
      builder.Append("    <ImplicitlyExpandDesignTimeFacades>false</ImplicitlyExpandDesignTimeFacades>").Append(k_WindowsNewline);
      builder.Append("  </PropertyGroup>").Append(k_WindowsNewline);
      builder.Append("  <ItemGroup>").Append(k_WindowsNewline);
      builder.Append("    <Reference Include=\"UnityEngine\">").Append(k_WindowsNewline);
      builder.Append("      <HintPath>").Append(InternalEditorUtility.GetEngineAssemblyPath()).Append("</HintPath>").Append(k_WindowsNewline);
      builder.Append("    </Reference>").Append(k_WindowsNewline);
      builder.Append("    <Reference Include=\"UnityEditor\">").Append(k_WindowsNewline);
      builder.Append("      <HintPath>").Append(InternalEditorUtility.GetEditorAssemblyPath()).Append("</HintPath>").Append(k_WindowsNewline);
      builder.Append("    </Reference>").Append(k_WindowsNewline);
      builder.Append("  </ItemGroup>").Append(k_WindowsNewline);
      builder.Append("  <ItemGroup>").Append(k_WindowsNewline);
      builder.Append(k_WindowsNewline);
      return builder;
    }

    static string GetSolutionText()
    {
      return string.Join("\r\n",
        @"",
        @"Microsoft Visual Studio Solution File, Format Version {0}",
        @"# Visual Studio {1}",
        @"{2}",
        @"Global",
        @"    GlobalSection(SolutionConfigurationPlatforms) = preSolution",
        @"        Debug|Any CPU = Debug|Any CPU",
        @"        Release|Any CPU = Release|Any CPU",
        @"    EndGlobalSection",
        @"    GlobalSection(ProjectConfigurationPlatforms) = postSolution",
        @"{3}",
        @"    EndGlobalSection",
        @"    GlobalSection(SolutionProperties) = preSolution",
        @"        HideSolutionNode = FALSE",
        @"    EndGlobalSection",
        @"EndGlobal",
        @"").Replace("    ", "\t");
    }

    static string GetProjectFooterTemplate()
    {
      return string.Join("\r\n",
        @"  </ItemGroup>",
        @"  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />",
        @"  <!-- To modify your build process, add your task inside one of the targets below and uncomment it. ",
        @"       Other similar extension points exist, see Microsoft.Common.targets.",
        @"  <Target Name=""BeforeBuild"">",
        @"  </Target>",
        @"  <Target Name=""AfterBuild"">",
        @"  </Target>",
        @"  -->",
        @"</Project>",
        @"");
    }

    void SyncSolution(IEnumerable<Assembly> islands)
    {
      SyncSolutionFileIfNotChanged(SolutionFile(), SolutionText(islands));
    }

    string SolutionText(IEnumerable<Assembly> islands)
    {
      var fileversion = "11.00";
      var vsversion = "2010";

      var relevantIslands = RelevantIslandsForMode(islands);
      string projectEntries = GetProjectEntries(relevantIslands);
      string projectConfigurations = string.Join(k_WindowsNewline,
        relevantIslands.Select(i => GetProjectActiveConfigurations(ProjectGuid(i.outputPath))).ToArray());
      return string.Format(CultureInfo.InvariantCulture, GetSolutionText(), fileversion, vsversion, projectEntries, projectConfigurations);
    }

    static IEnumerable<Assembly> RelevantIslandsForMode(IEnumerable<Assembly> islands)
    {
      IEnumerable<Assembly> relevantIslands = islands.Where(i => ScriptingLanguage.CSharp == ScriptingLanguageFor(i));
      return relevantIslands;
    }

    /// <summary>
    /// Get a Project("{guid}") = "MyProject", "MyProject.unityproj", "{projectguid}"
    /// entry for each relevant language
    /// </summary>
    string GetProjectEntries(IEnumerable<Assembly> islands)
    {
      var projectEntries = islands.Select(i => string.Format(CultureInfo.InvariantCulture,
        m_SolutionProjectEntryTemplate,
        SolutionGuid(i), Utility.FileNameWithoutExtension(i.outputPath), Path.GetFileName(ProjectFile(i)),
        ProjectGuid(i.outputPath)
      ));

      return string.Join(k_WindowsNewline, projectEntries.ToArray());
    }

    /// <summary>
    /// Generate the active configuration string for a given project guid
    /// </summary>
    string GetProjectActiveConfigurations(string projectGuid)
    {
      return string.Format(CultureInfo.InvariantCulture,
        m_SolutionProjectConfigurationTemplate,
        projectGuid);
    }

    string EscapedRelativePathFor(string file)
    {
      var projectDir = ProjectDirectory.Replace('/', '\\');
      file = file.Replace('/', '\\');
      var path = SkipPathPrefix(file, projectDir);

      var packageInfo = m_AssemblyNameProvider.FindForAssetPath(path.Replace('\\', '/'));
      if (packageInfo != null)
      {
        // We have to normalize the path, because the PackageManagerRemapper assumes
        // dir seperators will be os specific.
        var absolutePath = Path.GetFullPath(NormalizePath(path)).Replace('/', '\\');
        path = SkipPathPrefix(absolutePath, projectDir);
      }

      return SecurityElement.Escape(path);
    }

    static string SkipPathPrefix(string path, string prefix)
    {
      if (path.Replace("\\", "/").StartsWith($"{prefix}/", StringComparison.Ordinal))
        return path.Substring(prefix.Length + 1);
      return path;
    }

    static string NormalizePath(string path)
    {
      if (Path.DirectorySeparatorChar == '\\')
        return path.Replace('/', Path.DirectorySeparatorChar);
      return path.Replace('\\', Path.DirectorySeparatorChar);
    }


    string ProjectGuid(string assembly)
    {
      return SolutionGuidGenerator.GuidForProject(m_ProjectName + Utility.FileNameWithoutExtension(assembly));
    }

    string SolutionGuid(Assembly island)
    {
      return SolutionGuidGenerator.GuidForSolution(m_ProjectName, GetExtensionOfSourceFiles(island.sourceFiles));
    }

    static string ProjectFooter()
    {
      return GetProjectFooterTemplate();
    }

    static string GetProjectExtension()
    {
      return ".csproj";
    }
  }

  public static class SolutionGuidGenerator
  {
    public static string GuidForProject(string projectName)
    {
      return ComputeGuidHashFor(projectName + "salt");
    }

    public static string GuidForSolution(string projectName, string sourceFileExtension)
    {
      if (sourceFileExtension.ToLowerInvariant() == "cs")
        // GUID for a C# class library: http://www.codeproject.com/Reference/720512/List-of-Visual-Studio-Project-Type-GUIDs
        return "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";

      return ComputeGuidHashFor(projectName);
    }

    static string ComputeGuidHashFor(string input)
    {
      var hash = MD5.Create().ComputeHash(Encoding.Default.GetBytes(input));
      return HashAsGuid(HashToString(hash));
    }

    static string HashAsGuid(string hash)
    {
      var guid = hash.Substring(0, 8) + "-" + hash.Substring(8, 4) + "-" + hash.Substring(12, 4) + "-" +
                 hash.Substring(16, 4) + "-" + hash.Substring(20, 12);
      return guid.ToUpperInvariant();
    }

    static string HashToString(byte[] bs)
    {
      var sb = new StringBuilder();
      foreach (byte b in bs)
        sb.Append(b.ToString("x2"));
      return sb.ToString();
    }
  }
}