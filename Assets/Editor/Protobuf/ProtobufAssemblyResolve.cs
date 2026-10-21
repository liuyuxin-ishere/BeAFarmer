using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Game.Editor.ProtobufTools
{
    /// <summary>
    /// 编辑器域内解析 Google.Protobuf 对 System.Runtime.CompilerServices.Unsafe 的精确版本依赖。
    /// </summary>
    [InitializeOnLoad]
    static class ProtobufAssemblyResolve
    {
        static ProtobufAssemblyResolve()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name);
            if (name.Name != "System.Runtime.CompilerServices.Unsafe")
                return null;

            string[] candidates =
            {
                Path.Combine(Application.dataPath, "Plugins/Google.Protobuf/System.Runtime.CompilerServices.Unsafe.dll"),
                Path.Combine(Application.dataPath, "Plugins/System.Runtime.CompilerServices.Unsafe/System.Runtime.CompilerServices.Unsafe.dll"),
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path))
                    continue;
                try
                {
                    return Assembly.LoadFrom(path);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Protobuf] Failed to load Unsafe from {path}: {ex.Message}");
                }
            }

            return null;
        }
    }
}
