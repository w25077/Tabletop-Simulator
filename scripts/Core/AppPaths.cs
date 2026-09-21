using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 存档根目录下的路径约定。一个「存档」= 一个玩法原型方案 = 一个自包含目录：
/// <code>
/// &lt;saveRoot&gt;/
///   saves/
///     index.json               存档列表（名称 / 最后修改时间）
///     存档1/
///       project.json           卡牌定义 / 卡组 / 区域 / 桌面主题
///       state.json             当前对局中所有物件的位置、朝向、正反、堆叠
///       images/                这个存档导入的图片（每个存档独立，互不干扰）
///       thumbs/                存档缩略图
///   logs/
///     history.jsonl            操作日志（每行一条，可 diff、可二次处理）
///   last_save.txt              上次打开的存档名（一行文本）
/// </code>
///
/// Windows 上默认根是 <c>%APPDATA%\Godot\app_userdata\Tabletop Simulator\</c>。
///
/// <b>为什么允许换根（<c>--save-root</c>）：</b>沙箱里 <c>user://</c> 不可写，
/// 而"存 → 重启 → 读 → 逐字段比对"这条循环恰恰是 M4 最值钱的一条断言 ——
/// 手测过就算数的存档功能等于没验。把根指到工作区里的临时目录，
/// 每次自检都能把整条循环重跑一遍，真实存档也绝不会被自检碰脏。
/// </summary>
public static class AppPaths
{
	/// <summary>默认根（<c>user://</c>）。</summary>
	public const string DefaultRoot = "user://";

	/// <summary>命令行开关。<c>--save-root &lt;路径&gt;</c></summary>
	public const string RootFlag = "--save-root";

	private static string _root = DefaultRoot;

	/// <summary>
	/// 当前存档根。以 <c>/</c> 结尾，可直接拼相对路径。
	///
	/// 由 <see cref="Initialize"/> 决定：命令行 &gt; <c>user://</c>。
	/// 刻意不做成"每处各自读一次命令行"—— 那种写法会在某个漏改的调用点
	/// 悄悄退回默认根，而症状是"自检把真实存档改乱了"。
	/// </summary>
	public static string Root => _root;

	/// <summary>本次运行是否用了自定义根（自检的元断言要核对这件事）。</summary>
	public static bool UsingCustomRoot => !string.Equals(_root, DefaultRoot, System.StringComparison.Ordinal);

	/// <summary>
	/// 启动时定根。<b>必须在使用任何路径之前调用</b>（见 <c>Main._Ready</c> 的说明）。
	///
	/// 命令行给相对路径时按工作目录解析 —— <c>shot.ps1</c> 传的就是相对路径，
	/// 而 Godot 的工作目录是项目根，于是自检的存档落在 <c>.dev/userdata/</c> 下。
	/// </summary>
	public static void Initialize(string rawRoot)
	{
		if (string.IsNullOrWhiteSpace(rawRoot))
		{
			_root = DefaultRoot;
			return;
		}

		string root = rawRoot.Replace('\\', '/').Trim();

		if (!root.EndsWith('/'))
			root += "/";

		_root = root;
	}

	public static string SavesRoot => $"{_root}saves";

	public static string SaveIndexFile => $"{SavesRoot}/index.json";

	public static string LogsDir => $"{_root}logs";

	public static string HistoryLogFile => $"{LogsDir}/history.jsonl";

	/// <summary>上次打开的存档名（一行文本）。不存在 = 首次运行。</summary>
	public static string LastSaveFile => $"{_root}last_save.txt";

	public const string DefaultSaveName = "存档1";

	public static string SaveDir(string saveName) => $"{SavesRoot}/{Sanitize(saveName)}";

	public static string ImagesDir(string saveName) => $"{SaveDir(saveName)}/images";

	public static string ThumbsDir(string saveName) => $"{SaveDir(saveName)}/thumbs";

	public static string ProjectFile(string saveName) => $"{SaveDir(saveName)}/project.json";

	public static string StateFile(string saveName) => $"{SaveDir(saveName)}/state.json";

	/// <summary>某张导入图片的完整路径（存档目录可整体搬走 —— 所以只存文件名）。</summary>
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

	/// <summary>递归创建目录（<c>user://</c> / <c>res://</c> / 绝对路径都支持）。返回是否成功。</summary>
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

	/// <summary>列出根下所有存档目录名（不含 index.json）。</summary>
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

	/// <summary>写"上次打开的存档"。失败只警告 —— 它丢了最多是下次退回示例内容。</summary>
	public static void WriteLastSave(string saveName)
	{
		EnsureDir(_root);
		using FileAccess? f = FileAccess.Open(LastSaveFile, FileAccess.ModeFlags.Write);
		if (f is null)
		{
			GD.PushWarning($"[AppPaths] 写不了 {LastSaveFile}，下次启动会退回示例内容");
			return;
		}

		f.StoreString(saveName);
	}

	/// <summary>读"上次打开的存档"；没有或读不出则返回空串。</summary>
	public static string ReadLastSave()
	{
		if (!FileAccess.FileExists(LastSaveFile))
			return string.Empty;

		using FileAccess? f = FileAccess.Open(LastSaveFile, FileAccess.ModeFlags.Read);
		return f?.GetAsText().Trim() ?? string.Empty;
	}

	/// <summary>把 <c>user://</c> 路径转成系统绝对路径（打日志 / 给用户看时用）。</summary>
	public static string ToAbsolute(string userPath) => ProjectSettings.GlobalizePath(userPath);
}
