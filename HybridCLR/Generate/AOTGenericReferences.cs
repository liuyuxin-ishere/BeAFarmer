using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// HybridCLR AOT 泛型补充清单（PoC #4 手工固化关键项；完整列表可在装 IL2CPP 后 Generate All 覆盖）。
/// </summary>
public class AOTGenericReferences : MonoBehaviour
{
    // {{ AOT assemblies
    public static readonly IReadOnlyList<string> PatchedAOTAssemblyList = new List<string>
    {
        "Google.Protobuf.dll",
    };
    // }}

    // {{ constraint implement type
    // }}

    // {{ AOT generic types
    // }}

    public void RefMethods()
    {
        // 运行时真正的泛型实例由热更 Entry.TouchAotGenerics / GenerateAll 扫描补充。
    }
}
