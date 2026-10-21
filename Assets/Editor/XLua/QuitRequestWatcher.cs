using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Editor.XLuaTools
{
    /// <summary>
    /// 监视验收目录下的 quit-request 标记。出现后先退出 Play，再延迟保存编辑态场景并正常退出。
    /// 用于主工程已打开时，避免强杀或开第二实例。Play 中直接 SaveOpenScenes 会抛错。
    /// </summary>
    [InitializeOnLoad]
    static class QuitRequestWatcher
    {
        const string RelativeMarker = "Docs/Acceptance/xLuaMigration/.coordinator/quit-request.txt";
        static double _nextCheck;
        static bool _pendingQuit;
        static double _saveAfter;
        static bool _markerConsumed;

        static QuitRequestWatcher()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        /// <summary>Play 模式变化：离开 Play 后安排延迟保存退出。</summary>
        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!_pendingQuit)
                return;
            if (state == PlayModeStateChange.EnteredEditMode)
                _saveAfter = EditorApplication.timeSinceStartup + 0.5;
        }

        /// <summary>每 2 秒检查标记；若已安排退出则在离开 Play 后延迟保存并 Exit。</summary>
        static void Tick()
        {
            if (_pendingQuit)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                if (EditorApplication.timeSinceStartup < _saveAfter)
                    return;

                _pendingQuit = false;
                Debug.Log("[xLua] quit-request: saving edit-mode scenes/assets and exiting");
                EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();
                EditorApplication.Exit(0);
                return;
            }

            if (EditorApplication.timeSinceStartup < _nextCheck)
                return;
            _nextCheck = EditorApplication.timeSinceStartup + 2.0;

            var marker = Path.GetFullPath(Path.Combine(Application.dataPath, "..", RelativeMarker));
            if (!File.Exists(marker))
                return;

            if (!_markerConsumed)
            {
                _markerConsumed = true;
                try
                {
                    File.Delete(marker);
                }
                catch
                {
                    // 删不掉也继续退出，避免卡死。
                }
            }

            Debug.Log("[xLua] quit-request observed");
            _pendingQuit = true;
            if (EditorApplication.isPlaying)
            {
                Debug.Log("[xLua] quit-request: exiting Play before save");
                EditorApplication.isPlaying = false;
                _saveAfter = double.MaxValue; // 等 EnteredEditMode 再设延迟
            }
            else
            {
                _saveAfter = EditorApplication.timeSinceStartup + 0.5;
            }
        }
    }
}
