using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 操作日志落盘（<c>&lt;saveRoot&gt;/logs/history.jsonl</c>，一行一条 JSON）。
///
/// <b>为什么值得落盘：</b>会话里的面板一关就没了，而"我昨天是怎么把那摞牌搞乱的"
/// 正是这个工具要回答的问题。JSONL 每行独立，可以直接 diff、也可以二次处理 ——
/// 这比存一整个 JSON 数组实用：追加不用重写全文件，坏了一行也只坏一行。
///
/// 用 <c>Json.Stringify</c>（Godot 的）而不是 <c>System.Text.Json</c>：
/// 这里写的是"给人看的流水账"，一个扁平字典，不需要 <see cref="SaveJson"/> 那套
/// 类型转换器；而且它天然不转义中文。
/// </summary>
public static class HistoryLog
{
	private static bool _warned;

	/// <summary>成功追加的条数（自检用）。</summary>
	public static int Written { get; private set; }

	/// <summary>最后一次失败的原因；空串表示没有失败过。</summary>
	public static string LastError { get; private set; } = "";

	/// <summary>最后一次写到的路径 —— 用来核对"根是不是我以为的那个"。</summary>
	public static string LastPath { get; private set; } = "";

	/// <summary>
	/// 追加一条。任何失败都只警告一次 —— 记日志失败绝不能影响摆牌，
	/// 但也不能彻底静默（不然"日志文件一直是空的"会查不出原因）。
	/// </summary>
	public static void Append(int index, string label, string mergeKey, int frame, int cursor)
	{
		AppPaths.EnsureDir(AppPaths.LogsDir);

		string path = AppPaths.HistoryLogFile;
		LastPath = path;

		// <b>ReadWrite 不会创建文件。</b>
		//
		// 第一版就是直接 Open(ReadWrite)：第一次运行时文件还不存在，
		// 于是拿到 FileNotFound、一条也写不进去 —— 而症状是
		// "日志文件一直是空的、连目录都建好了，却什么也没报"。
		// 目录已经被 EnsureDir 建出来了，所以问题完全不在权限上。
		//
		// 处置：先按"创建（若不存在）"打开一次，再按追加语义写。
		// Write 模式会截断已有内容，所以只在那一次用；之后一律 ReadWrite + SeekEnd。
		if (!FileAccess.FileExists(path))
		{
			using FileAccess? created = FileAccess.Open(path, FileAccess.ModeFlags.Write);
			if (created is null)
			{
				LastError = $"{FileAccess.GetOpenError()}（创建）@ {path}";
				WarnOnce($"建不了 {path}：{FileAccess.GetOpenError()}");
				return;
			}
		}

		using FileAccess? f = FileAccess.Open(path, FileAccess.ModeFlags.ReadWrite);
		if (f is null)
		{
			LastError = $"{FileAccess.GetOpenError()} @ {path}";
			WarnOnce($"打不开 {path}：{FileAccess.GetOpenError()}");
			return;
		}

		f.SeekEnd();

		var row = new Godot.Collections.Dictionary
		{
			// 墙上时间留给人看（"昨天 22:10 那次"），帧号留给自检（唯一可靠的时间）
			["time"] = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
			["frame"] = frame,
			["index"] = index,
			["cursor"] = cursor,
			["label"] = label,

			// 合并键：空串 = 这条不参与连击合并。
			// 有了它，"连转 4 次 15° 被并成一条"这件事在日志里是看得见的。
			["mergeKey"] = mergeKey,
		};

		f.StoreLine(Json.Stringify(row));
		Written++;
	}

	/// <summary>已落盘的条数（自检用：断言日志真的在长，而不是文件一直在被重建）。</summary>
	public static int LineCount()
	{
		if (!FileAccess.FileExists(AppPaths.HistoryLogFile))
			return 0;

		using FileAccess? f = FileAccess.Open(AppPaths.HistoryLogFile, FileAccess.ModeFlags.Read);
		if (f is null)
			return 0;

		int lines = 0;
		while (!f.EofReached())
		{
			string line = f.GetLine();
			if (!string.IsNullOrWhiteSpace(line))
				lines++;
		}

		return lines;
	}

	/// <summary>清空（换存档时用 —— 上一个存档的操作流水对新存档没有意义）。</summary>
	public static void Clear()
	{
		AppPaths.EnsureDir(AppPaths.LogsDir);
		using FileAccess? f = FileAccess.Open(AppPaths.HistoryLogFile, FileAccess.ModeFlags.Write);
		f?.StoreString(string.Empty);
	}

	private static void WarnOnce(string message)
	{
		if (_warned)
			return;

		_warned = true;
		GD.PushWarning($"[HistoryLog] {message}（后续同类失败不再重复告警）");
	}
}
