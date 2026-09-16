// 判定 Environment.ExitCode 在 Unity 2022.3 batchmode 下是否真的成为进程退出码。
// 两个方法，分别用 Unity **官方文档承认**的机制与项目里**实际在用**的机制。
using System;
using UnityEditor;
using UnityEngine;

public static class NTExitProbe
{
    // 项目里所有探针用的都是这一招
    public static void EnvExitCode()
    {
        Debug.Log("[NTExitProbe] 设置 Environment.ExitCode = 7，然后正常返回（不调用 EditorApplication.Exit）");
        Environment.ExitCode = 7;
    }

    // Unity 官方文档承认的机制
    public static void AppExit()
    {
        Debug.Log("[NTExitProbe] 调用 EditorApplication.Exit(7)");
        EditorApplication.Exit(7);
    }

    // 对照组：什么都不做
    public static void Noop()
    {
        Debug.Log("[NTExitProbe] 什么都不做，正常返回");
    }
}
