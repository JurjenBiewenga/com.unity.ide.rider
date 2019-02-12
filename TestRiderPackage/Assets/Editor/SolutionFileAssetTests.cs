using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using System.IO;
using System;

namespace RiderEditor.Runtime_spec
{
    [TestFixture]
    [Serializable]
    public class SolutionProject
    {
        const string k_EmptyCSharpScript = @"
using UnityEngine;
public class SimpleCSharpScript : MonoBehaviour
{
  void Start()
  {
  }
}";
        [SerializeField]
        IGenerator m_ProjectGeneration;
        [SerializeField]
        List<string> m_GeneratedFiles = new List<string>();
        [SerializeField]
        string m_SolutionPath;

        static string ProjectName
        {
            get
            {
                string[] s = Application.dataPath.Split('/');
                string projectName = s[s.Length - 2];
                return projectName;
            }
        }
        static string s_SolutionFile = $"{ProjectName}.sln";

        [SetUp]
        public void SetUp() {
            var projectDirectory = Directory.GetParent(Application.dataPath).FullName;
            m_ProjectGeneration = new ProjectGeneration(projectDirectory);
        }

        [TearDown]
        public void Dispose()
        {
            m_GeneratedFiles.ForEach(File.Delete);
            m_GeneratedFiles.Clear();
            AssetDatabase.Refresh();
        }

        [UnityTest]
        public IEnumerator FormattedSolution()
        {
            string GetProjectName()
            {
                string[] s = Application.dataPath.Split('/');
                return s[s.Length - 2];
            }

            string GetProjectGUID(string projectName)
            {
                return SolutionGuidGenerator.GuidForProject(GetProjectName() + projectName);
            }

            string GetSolutionGUID(string projectName)
            {
                return SolutionGuidGenerator.GuidForSolution(projectName, "cs");
            }

            m_SolutionPath = m_ProjectGeneration.SolutionFile();
            CopyScriptToAssetsFolder(Application.dataPath, "SimpleCSharpScript.cs", k_EmptyCSharpScript);

            AssetDatabase.Refresh();
            m_ProjectGeneration.Sync();

            yield return new RecompileScripts(true);

            string solutionContents = File.ReadAllText(m_SolutionPath);

            // solutionguid, solutionname, projectguid
            var solutionExpected = string.Join("\r\n", new[]
            {
                @"",
                @"Microsoft Visual Studio Solution File, Format Version 11.00",
                @"# Visual Studio 2010",
                @"Project(""{{{0}}}"") = ""{6}"", ""{6}.csproj"", ""{{{1}}}""",
                @"EndProject",
                @"Project(""{{{0}}}"") = ""{7}"", ""{7}.csproj"", ""{{{2}}}""",
                @"EndProject",
                @"Project(""{{{0}}}"") = ""{8}"", ""{8}.csproj"", ""{{{3}}}""",
                @"EndProject",
                @"Project(""{{{0}}}"") = ""{9}"", ""{9}.csproj"", ""{{{4}}}""",
                @"EndProject",
                @"Project(""{{{0}}}"") = ""{10}"", ""{10}.csproj"", ""{{{5}}}""",
                @"EndProject",
                @"Global",
                @"    GlobalSection(SolutionConfigurationPlatforms) = preSolution",
                @"        Debug|Any CPU = Debug|Any CPU",
                @"        Release|Any CPU = Release|Any CPU",
                @"    EndGlobalSection",
                @"    GlobalSection(ProjectConfigurationPlatforms) = postSolution",
                @"        {{{1}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
                @"        {{{1}}}.Debug|Any CPU.Build.0 = Debug|Any CPU",
                @"        {{{1}}}.Release|Any CPU.ActiveCfg = Release|Any CPU",
                @"        {{{1}}}.Release|Any CPU.Build.0 = Release|Any CPU",
                @"        {{{2}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
                @"        {{{2}}}.Debug|Any CPU.Build.0 = Debug|Any CPU",
                @"        {{{2}}}.Release|Any CPU.ActiveCfg = Release|Any CPU",
                @"        {{{2}}}.Release|Any CPU.Build.0 = Release|Any CPU",
                @"        {{{3}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
                @"        {{{3}}}.Debug|Any CPU.Build.0 = Debug|Any CPU",
                @"        {{{3}}}.Release|Any CPU.ActiveCfg = Release|Any CPU",
                @"        {{{3}}}.Release|Any CPU.Build.0 = Release|Any CPU",
                @"        {{{4}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
                @"        {{{4}}}.Debug|Any CPU.Build.0 = Debug|Any CPU",
                @"        {{{4}}}.Release|Any CPU.ActiveCfg = Release|Any CPU",
                @"        {{{4}}}.Release|Any CPU.Build.0 = Release|Any CPU",
                @"        {{{5}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
                @"        {{{5}}}.Debug|Any CPU.Build.0 = Debug|Any CPU",
                @"        {{{5}}}.Release|Any CPU.ActiveCfg = Release|Any CPU",
                @"        {{{5}}}.Release|Any CPU.Build.0 = Release|Any CPU",
                @"    EndGlobalSection",
                @"    GlobalSection(SolutionProperties) = preSolution",
                @"        HideSolutionNode = FALSE",
                @"    EndGlobalSection",
                @"EndGlobal",
                @""
            }).Replace("    ", "\t");

            var solutionTemplate = string.Format(
                solutionExpected,
                GetSolutionGUID(GetProjectName()),
                GetProjectGUID("AsmdefResponse"),
                GetProjectGUID("Assembly-CSharp"),
                GetProjectGUID("Assembly-CSharp-Editor"),
                GetProjectGUID("Unity.Rider.Editor"),
                GetProjectGUID("Unity.Rider.EditorTests"),
                "AsmdefResponse",
                "Assembly-CSharp",
                "Assembly-CSharp-Editor",
                "Unity.Rider.Editor",
                "Unity.Rider.EditorTests");

            Assert.AreEqual(solutionTemplate, solutionContents);
        }

        [SerializeField]
        string m_SolutionText;

        [UnityTest]
        public IEnumerator ResyncDoesNotChangeSolution()
        {
            CopyScriptToAssetsFolder(Application.dataPath, "foo.cs", " ");

            yield return new RecompileScripts(true);
            m_ProjectGeneration.Sync();

            m_SolutionText = File.ReadAllText(s_SolutionFile);

            yield return new RecompileScripts(false);
            m_ProjectGeneration.Sync();

            string secondSyncSolutionTest = File.ReadAllText(s_SolutionFile);
            Assert.AreEqual(m_SolutionText, secondSyncSolutionTest, "Solution changed after second sync");

            yield return new RecompileScripts(false);
            m_ProjectGeneration.Sync();

            string thirdSyncSolutionText = File.ReadAllText(s_SolutionFile);
            Assert.AreEqual(m_SolutionText, thirdSyncSolutionText, "Solution changed after third sync");
        }

        [UnityTest]
        public IEnumerator EmptySolutionSettingsSucceeds()
        {
           string originalText = @"Microsoft Visual Studio Solution File, Format Version 10.00
# Visual Studio 2008
Global
EndGlobal";
            // Pre-seed solution file with empty property section
            File.WriteAllText(s_SolutionFile, originalText);

            CopyScriptToAssetsFolder(Application.dataPath, "foo.cs", " ");

            yield return new RecompileScripts(true);
            m_ProjectGeneration.Sync();

            string syncedSolutionText = File.ReadAllText(s_SolutionFile);
            Assert.True(syncedSolutionText.Length != 0);
        }

        void CopyScriptToAssetsFolder(string assetPath, string fileName, string content)
        {
            var targetFile = Path.Combine(assetPath, fileName);
            m_GeneratedFiles.Add(targetFile);
            File.WriteAllText(targetFile, content);
        }
    }
}