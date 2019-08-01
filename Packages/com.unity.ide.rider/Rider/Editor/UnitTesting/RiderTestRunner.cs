using JetBrains.Annotations;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Packages.Rider.Editor.UnitTesting
{
  public static class RiderTestRunner
  {
    private static readonly TestsCallback Callback = ScriptableObject.CreateInstance<TestsCallback>();
    
    [UsedImplicitly]
    public static void RunTests(int? testMode, string[] assemblyNames, string[] testNames, string[] categoryNames, string[] groupNames, int? buildTarget)
    {
      CallbackData.instance.isRider = true;
            
      var api = ScriptableObject.CreateInstance<TestRunnerApi>();
      var mode =(TestMode?)testMode; // for future use, when test-framework allows running Edit and Play at once
      var settings = new ExecutionSettings();
      var filter = new Filter
      {
        assemblyNames = assemblyNames,
        testNames = testNames,
        categoryNames = categoryNames,
        groupNames = groupNames,
        targetPlatform = (BuildTarget?) buildTarget
      };
      
      if (mode != null)
        filter.testMode = (TestMode) mode;
      
      settings.filters = new []{
        filter
      };
      api.Execute(settings);
      
      api.UnregisterCallbacks(Callback); // avoid multiple registrations
      api.RegisterCallbacks(Callback); // This can be used to receive information about when the test suite and individual tests starts and stops. Provide this with a scriptable object implementing ICallbacks
    }
  }
}