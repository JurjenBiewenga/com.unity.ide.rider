using System;
using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using Packages.Rider.Editor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Packages.Rider.Tests.Editor
{
    [TestFixture]
    public class CSProjectFileTests
    {

        [Test]
        public void FilesNotContributedAnAssemblyWillNotGetAdded()
        {
            var mock = new Mock<IAssemblyNameProvider>();
            var files = new[]
            {
                "File.cs",
            };
            var island = new Assembly("Assembly2", "/User/Test/Assembly2.dll", files, new string[0], new Assembly[0], new string[0], AssemblyFlags.None);
            mock.Setup(x => x.GetEditorAssemblies(It.IsAny<Func<string, bool>>())).Returns(new[] { island });
            mock.Setup(x => x.GetAssemblyNameFromScriptPath(It.IsAny<string>())).Returns(string.Empty);
            mock.Setup(x => x.GetAllAssetPaths()).Returns(new[] { "File/Not/In/Assembly.hlsl" });

            var synchronizer = new ProjectGeneration(Directory.GetParent(Application.dataPath).FullName, mock.Object);
            var syncPaths = new Dictionary<string, string>();
            synchronizer.Settings = new TestSettings { ShouldSync = false, SyncPath = syncPaths };

            synchronizer.Sync();
            var csprojContent = syncPaths[synchronizer.ProjectFile(island)];
            StringAssert.DoesNotContain("NotExist.hlsl", csprojContent);
        }

        [Test]
        public void RelativePackages_GetsPathResolvedCorrectly()
        {
            var mock = new Mock<IAssemblyNameProvider>();
            var files = new[]
            {
                "/FullPath/ExamplePackage/Packages/Asset.cs",
            };
            var island = new Assembly("ExamplePackage", "/FullPath/Example/ExamplePackage/ExamplePackage.dll", files, new string[0], new Assembly[0], new string[0], AssemblyFlags.None);
            mock.Setup(x => x.GetEditorAssemblies(It.IsAny<Func<string, bool>>())).Returns(new[] { island });
            mock.Setup(x => x.GetAssemblyNameFromScriptPath(It.IsAny<string>())).Returns(string.Empty);
            mock.Setup(x => x.GetAllAssetPaths()).Returns(new[] { "/FullPath/ExamplePackage/Packages/Asset.cs" });
            mock.Setup(x => x.FindForAssetPath("/FullPath/ExamplePackage/Packages/Asset.cs")).Returns(default(UnityEditor.PackageManager.PackageInfo));

            var synchronizer = new ProjectGeneration("/FullPath/Example", mock.Object);
            var syncPaths = new Dictionary<string, string>();
            synchronizer.Settings = new TestSettings { ShouldSync = false, SyncPath = syncPaths };

            synchronizer.Sync();

            StringAssert.Contains("\\FullPath\\ExamplePackage\\Packages\\Asset.cs", syncPaths[Path.Combine("/FullPath/Example", $"{island.name}.csproj")]);
        }

        [Test]
        public void PlayerAssemblies_WillGetWritten()
        {
            var mock = new Mock<IAssemblyNameProvider>();
            var files = new[]
            {
                "File.cs",
            };

            var island = new Assembly("Assembly", "/User/Test/Assembly.dll", files, new string[0], new Assembly[0], new string[0], AssemblyFlags.None);
            mock.Setup(x => x.GetPlayerAssemblies(It.IsAny<Func<string, bool>>())).Returns(new[] { island });
            mock.Setup(x => x.GetAssemblyNameFromScriptPath(It.IsAny<string>())).Returns(string.Empty);
            mock.Setup(x => x.GetAllAssetPaths()).Returns(files);

            var synchronizer = new ProjectGeneration(Directory.GetParent(Application.dataPath).FullName, mock.Object);
            var syncPaths = new Dictionary<string, string>();
            synchronizer.Settings = new TestSettings { ShouldSync = false, SyncPath = syncPaths };

            synchronizer.Sync();

            Assert.True(syncPaths.ContainsKey(synchronizer.ProjectFile(island)), "Player project CS Project should be written.");
            StringAssert.Contains("Assembly-player.csproj", synchronizer.ProjectFile(island));
        }
    }
}