// Editor/BodyFitVersion.cs
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace Hinata.BodyFit.Editor
{
    /// <summary>
    /// 開発テスト用の内部バージョン表示。
    /// package.json のバージョンに加え、パッケージ内の全スクリプトの内容から計算したハッシュを出す
    /// (一部のファイルだけ古い、といった更新漏れが分かる)。改行コードの違い (CRLF/LF) は無視する。
    /// </summary>
    internal static class BodyFitVersion
    {
        static string _text;

        internal static string Text => _text ??= Build();

        static string Build()
        {
            string version = "?";
            string root = null;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BodyFitVersion).Assembly);
            if (info != null)
            {
                version = info.version;
                root = info.resolvedPath;
            }
            else
            {
                // Assets に直接置いた場合: このスクリプトの 1 つ上のフォルダ
                var guid = AssetDatabase.FindAssets("BodyFitVersion t:MonoScript").FirstOrDefault();
                var path = guid != null ? AssetDatabase.GUIDToAssetPath(guid) : null;
                if (path != null) root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path)));
            }
            return $"Body Fit {version}  (dev {Hash(root)})";
        }

        static string Hash(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "????????";
            var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Select(f => (full: f, rel: Path.GetRelativePath(root, f).Replace('\\', '/')))
                .Where(f => !f.rel.StartsWith(".") && !f.rel.Contains("~/"))
                .OrderBy(f => f.rel, System.StringComparer.Ordinal)
                .ToList();
            using (var sha = SHA1.Create())
            {
                var sb = new StringBuilder();
                foreach (var (full, rel) in files)
                {
                    sb.Append(rel).Append('\n');
                    sb.Append(File.ReadAllText(full, Encoding.UTF8).Replace("\r", "")).Append('\n');
                }
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return string.Concat(h.Take(4).Select(b => b.ToString("x2")));
            }
        }
    }
}
