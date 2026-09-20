using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// <c>user://</c> 下的路径约定。一个「存档」= 一个玩法原型方案 = 一个自包含目录：
/// <code>
/// user://saves/
///   index.json                 存档列表（名称 / 最后修改时间）
///   存档1/
///     project.json             卡牌定义 / 卡组 / 区域 / 桌面主题
///     state.json               当前对局中所有物件的位置、朝向、正反、堆叠
///     images/                  这个存档导入的图片（每个存档独立，互不干扰）
///     thumbs/                  存档缩略图
/// </code>
/// Windows 上实际位置是 <c>%APPDATA%\Godot\app_userdata\Tabletop Simulator\saves\</c>。
/// </summary>
public static class AppPaths
{
	public const string SavesRoot = "user://saves";
	public const string SaveIndexFile = "user://saves/index.json";
	public const string DefaultSaveName = "存档1";

	public static string SaveDir(string saveName) => $"{SavesRoot}/{Sanitize(saveName)}";

	public static string ImagesDir(string saveName) => $"{SaveDir(saveName)}/images";

	public static string ThumbsDir(string saveName) => $"{SaveDir(saveName)}/thumbs";

	public static string ProjectFile(string saveName) => $"{SaveDir(saveName)}/project.json";

	public static string StateFile(string saveName) => $"{SaveDir(saveName)}/state.json";

	/// <summary>某张导入图片的完整 <c>user://</c> 路径。</summary>
	public static string ImageFile(string saveName, string fileName) =>
		$"{ImagesDir(saveName)}/{fileName}";

	/// <summary>把存档名收拾成合法的目录名（去掉路径分隔符与 Windows 非法字符）。</summary>
	public static string Sanitize(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return DefaultSaveName;

		var sb = new System.Text.StringBuilder(name.Length);
		foreach (char c in name.Trim())
		{
			if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || c < ' ')
				continue;
			sb.Append(c);
		}

		string cleaned = sb.ToString().Trim().TrimEnd('.');
		return string.IsNullOrEmpty(cleaned) ? DefaultSaveName : cleaned;
	}

	/// <summary>递归创建目录（<c>user://</c> / <c>res://</c> 前缀都支持）。返回是否成功。</summary>
	public static bool EnsureDir(string dir)
	{
		if (DirAccess.DirExistsAbsolute(dir))
			return true;

		Error err = DirAccess.MakeDirRecursiveAbsolute(dir);
		if (err != Error.Ok)
			GD.PushError($"[AppPaths] 无法创建目录 {dir}：{err}");

		return err == Error.Ok;
	}

	/// <summary>确保一个存档的目录骨架存在。</summary>
	public static bool EnsureSaveLayout(string saveName)
	{
		bool ok = EnsureDir(ImagesDir(saveName));
		EnsureDir(ThumbsDir(saveName));
		return ok;
	}

	/// <summary>列出 <c>user://saves/</c> 下所有存档目录名（不含 index.json）。</summary>
	public static string[] ListSaveNames()
	{
		EnsureDir(SavesRoot);

		var names = new System.Collections.Generic.List<string>();
		using DirAccess? dir = DirAccess.Open(SavesRoot);
		if (dir is null)
			return names.ToArray();

		foreach (string entry in dir.GetDirectories())
		{
			if (!string.IsNullOrWhiteSpace(entry))
				names.Add(entry);
		}

		names.Sort(System.StringComparer.OrdinalIgnoreCase);
		return names.ToArray();
	}

	/// <summary>把 <c>user://</c> 路径转成系统绝对路径（打日志 / 给用户看时用）。</summary>
	public static string ToAbsolute(string userPath) => ProjectSettings.GlobalizePath(userPath);
}
