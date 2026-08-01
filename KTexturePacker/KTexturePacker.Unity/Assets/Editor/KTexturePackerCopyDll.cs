using System.IO;
using UnityEditor;
using UnityEngine;

namespace KTexturePacker.Unity.Editor
{
    /// <summary>
    /// 把本工程的产物 DLL 拷贝到 Unity 工程根目录下的 AAA 文件夹，便于统一分发/存档。
    /// 目标目录 = Unity 工程根（Assets 的上级）下的 AAA 文件夹。
    /// 例：Unity 工程在 .../KTexturePacker.Unity，则目标为 .../KTexturePacker.Unity/AAA。
    /// </summary>
    public static class KTexturePackerCopyDll
    {
        // 每个 DLL 对应的源目录：
        //  - Parser 是预编译 DLL，直接放在 Editor 脚本目录下；
        //  - Unity.Editor 是 Unity 编译出的程序集，在 Library/ScriptAssemblies。
        private static readonly (string name, bool fromScriptAssemblies)[] Dlls =
        {
            ("KTexturePacker.Parser.dll", false),
            ("KTexturePacker.Unity.Editor.dll", true),
        };

        [MenuItem("KTexturePacker/Copy DLL")]
        private static void CopyDlls()
        {
            // Unity 工程根：Assets 的上级。
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            // 预编译的 Parser DLL 放在 Assets/KTexturePacker.Unity/Editor/ 下。
            string editorDir = Path.GetFullPath(Path.Combine(Application.dataPath, "KTexturePacker.Unity", "Editor"));
            string destDir = Path.Combine(projectRoot, "AAA");

            string scriptAssemblies = Path.Combine(projectRoot, "Library", "ScriptAssemblies");
            Directory.CreateDirectory(destDir);

            int copied = 0;
            foreach (var (name, fromScriptAssemblies) in Dlls)
            {
                string src = fromScriptAssemblies
                    ? Path.Combine(scriptAssemblies, name)
                    : Path.Combine(editorDir, name);
                if (!File.Exists(src))
                {
                    Debug.LogWarning($"[KTexturePacker] 源 DLL 不存在，跳过: {src}（可能需要先编译对应程序集）");
                    continue;
                }
                string dst = Path.Combine(destDir, name);
                File.Copy(src, dst, overwrite: true);
                copied++;
                Debug.Log($"[KTexturePacker] 已拷贝: {src}\n  -> {dst}");
            }

            if (copied > 0)
                Debug.Log($"[KTexturePacker] 完成，共拷贝 {copied} 个 DLL 到 {destDir}");
            else
                Debug.LogWarning("[KTexturePacker] 没有拷贝任何 DLL。");
        }
    }
}
