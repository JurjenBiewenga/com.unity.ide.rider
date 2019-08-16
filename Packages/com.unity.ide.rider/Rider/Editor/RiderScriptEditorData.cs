using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Packages.Rider.Editor
{
  public class RiderScriptEditorData : ScriptableSingleton<RiderScriptEditorData>
  {
    [Serializable]
    public class DataList
    {
      public List<Data> data;
    }
    
    [Serializable]
    public class Data
    {
      public Data(){}
      public Data(string path)
      {
        Path = path;
        Length = new FileInfo(path).Length;
      }
      public string Path;
      public long Length;
    }

    // sln/csproj files were changed
    internal void CallIfHasChanges( Action action){
      var workingDir = new DirectoryInfo(Directory.GetCurrentDirectory());
      var projects = workingDir.GetFiles("*.csproj");
      var sln = Path.Combine(workingDir.FullName, workingDir.Name + ".sln");

      var currentData = new DataList {data = new List<Data>()};
      currentData.data.Add(new Data(sln));
      foreach (var project in projects)
      {
        currentData.data.Add(new Data(project.FullName));
      }

      if (JsonUtility.ToJson(currentData) != JsonUtility.ToJson(projectFilesModifications))
      {
        projectFilesModifications = currentData;
        action();
      }
    }

    public static void InitProjectFilesWatcher()
    {
      var workingDir = new DirectoryInfo(Directory.GetCurrentDirectory());
      var projects = workingDir.GetFiles("*.csproj");
      var sln = Path.Combine(workingDir.FullName, workingDir.Name + ".sln");
      instance.projectFilesModifications.data.Clear();
      instance.projectFilesModifications.data.Add(new Data(sln));
      foreach (var project in projects)
      {
        instance.projectFilesModifications.data.Add( new Data(project.FullName));
      }
    }

    [SerializeField] internal string currentEditorVersion; 
    [SerializeField] internal bool shouldLoadEditorPlugin;
    [SerializeField] internal DataList projectFilesModifications = new DataList {data = new List<Data>()};

    public void Init()
    {
      if (string.IsNullOrEmpty(currentEditorVersion))
        Invalidate(RiderScriptEditor.CurrentEditor);
    }

    public void Invalidate(string editorInstallationPath)
    {
      currentEditorVersion = RiderPathLocator.GetBuildNumber(editorInstallationPath);
      if (!Version.TryParse(currentEditorVersion, out var version))
        shouldLoadEditorPlugin = false;

      shouldLoadEditorPlugin = version >= new Version("191.7141.156");
    }
  }
}