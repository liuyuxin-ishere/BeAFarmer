using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Temporary guard for the user-authorized M1 execution session.
[InitializeOnLoad]
internal static class M1BatteryGuard
{
    private const string ActiveFile = "Temp/M1Execution.active";
    private const string StopFile = "Temp/M1Execution.low-battery";
    private static double nextCheck;
    private static bool stopping;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    static M1BatteryGuard()
    {
        EditorApplication.update += Update;
    }

    private static void Update()
    {
        if (Application.platform != RuntimePlatform.WindowsEditor || !File.Exists(ActiveFile))
            return;

        if (EditorApplication.timeSinceStartup < nextCheck)
            return;
        nextCheck = EditorApplication.timeSinceStartup + 5;

        if (!stopping)
        {
            if (!GetSystemPowerStatus(out PowerStatus power))
                return;
            File.WriteAllText("Temp/M1Execution.power.json", "{\"batteryPercent\":" + power.BatteryLifePercent
                + ",\"acLineStatus\":" + power.ACLineStatus + ",\"observedAtUtc\":\"" + DateTime.UtcNow.ToString("O") + "\"}");
            if (power.BatteryLifePercent == 255 || power.BatteryLifePercent >= 30)
                return;
            File.WriteAllText(StopFile, DateTimeOffset.Now.ToString("O") + " Battery=" + power.BatteryLifePercent + "%");
            stopping = true;
            Debug.LogWarning("[M1] Battery below 30%. Saving scenes and closing Unity.");
        }

        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.isPlaying = false;
            return;
        }
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;

        try
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !scene.isDirty)
                    continue;
                string path = scene.path;
                if (string.IsNullOrEmpty(path))
                {
                    Directory.CreateDirectory("Assets/Scenes");
                    path = AssetDatabase.GenerateUniqueAssetPath("Assets/Scenes/M1LowBatteryRecovery.unity");
                }
                if (!EditorSceneManager.SaveScene(scene, path))
                    throw new IOException("Could not save scene: " + path);
            }
            AssetDatabase.SaveAssets();
            File.AppendAllText(StopFile, Environment.NewLine + "Scenes and assets saved; Unity exit requested.");
            File.Delete(ActiveFile);
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            File.AppendAllText(StopFile, Environment.NewLine + "Save failed: " + exception.Message);
            nextCheck = EditorApplication.timeSinceStartup + 5;
        }
    }
}
