using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 图片导入：把外部的一张图拷进<b>当前存档的 <c>images/</c> 目录</b>，并让改动立刻生效。
///
/// 三条设计约定：
/// <list type="number">
/// <item><b>定义里只存文件名，不存路径。</b>
///   <c>CardDefinition.FaceImage = "face_fire.png"</c>，真正的路径由
///   <see cref="ResolvePath"/> 拼出来（<c>&lt;saveRoot&gt;/saves/&lt;存档&gt;/images/…</c>）。
///   于是整个存档目录可以拷走、发给别人、塞进 git —— 这是"自包含存档"的另一半
///   （另一半是 <see cref="SaveSystem"/> 把定义与对局写成两个 JSON）。</item>
/// <item><b>重名不覆盖。</b>拷进来一个也叫 <c>face.png</c> 的图，会变成
///   <c>face_2.png</c>，而原来那张的卡不受影响。覆盖是"改一张卡的面，结果另一张卡也变了"
///   那类极难往回查的问题。</item>
/// <item><b>内容相同就复用已有的文件名。</b>反复"导入同一张图"不该在目录里堆出
///   <c>face.png</c> / <c>face_2.png</c> / <c>face_3.png</c> 三份一模一样的文件。</item>
/// </list>
///
/// <b>拷贝逻辑是纯的（只用 <c>FileAccess</c>），不碰 UI。</b>
/// 于是自检能直接调 <see cref="Copy"/> 断言"文件真的进去了、名字对不对、内容一致"——
/// 而不是"我点了一次 FileDialog 看着像成功了"。<c>FileDialog</c> 只是它的一个调用方。
/// </summary>
public static class ImageImport
{
	/// <summary>扩展名白名单。挡掉 <c>.exe</c> / <c>.txt</c> 之类 —— 放进去也没用，只会让目录变脏。</summary>
	private static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".svg" };

	/// <summary>上一次导入失败的原因（给 UI 显示）。</summary>
	public static string LastError { get; private set; } = "";

	/// <summary>本次运行成功导入的张数（自检核对用）。</summary>
	public static int ImportCount { get; private set; }

	/// <summary>
	/// 把 <paramref name="sourcePath"/> 拷进存档的 <c>images/</c>，返回<b>该存档内的文件名</b>
	/// （要写进定义的就是它）。失败返回空串，原因见 <see cref="LastError"/>。
	/// </summary>
	/// <param name="sourcePath">源文件路径。既收绝对路径（<c>FileDialog</c> 给的就是它），
	/// 也收工作目录相对路径（自检造一张临时图时用）。</param>
	public static string Copy(string saveName, string sourcePath)
	{
		LastError = "";

		if (string.IsNullOrWhiteSpace(saveName))
		{
			LastError = "还没有当前存档";
			return "";
		}

		if (string.IsNullOrWhiteSpace(sourcePath))
		{
			LastError = "没有选文件";
			return "";
		}

		string normalized = sourcePath.Replace('\\', '/').Trim();
		if (!FileAccess.FileExists(normalized))
		{
			LastError = $"找不到 {normalized}";
			return "";
		}

		string extension = ExtensionOf(normalized);
		if (!IsAllowed(extension))
		{
			LastError = $"不支持 {extension}（只收 {string.Join(" / ", AllowedExtensions)}）";
			return "";
		}

		byte[]? bytes = FileAccess.GetFileAsBytes(normalized);
		if (bytes is null || bytes.Length == 0)
		{
			LastError = $"读不出 {normalized} 的内容";
			return "";
		}

		AppPaths.EnsureDir(AppPaths.ImagesDir(saveName));

		string fileName = AllocateName(saveName, BaseNameOf(normalized), bytes);
		if (fileName.Length == 0)
		{
			LastError = "腾不出一个可用的文件名";
			return "";
		}

		string target = AppPaths.ImageFile(saveName, fileName);
		using (FileAccess? f = FileAccess.Open(target, FileAccess.ModeFlags.Write))
		{
			if (f is null)
			{
				LastError = $"写不了 {target}：{FileAccess.GetOpenError()}";
				return "";
			}

			f.StoreBuffer(bytes);
		}

		// 校验"真的写进去了" —— 不只看 API 返回值。
		// 存档这块的失败大多是静默的（返回 Ok 但文件不在、内容截断），
		// 而症状会在几步之后才显形（卡面空白 / 读档少图），那时已经很难往回追。
		byte[]? written = FileAccess.GetFileAsBytes(target);
		if (written is null || written.Length != bytes.Length)
		{
			LastError = $"{fileName} 写入不完整（{bytes.Length} → {written?.Length ?? 0} 字节）";
			return "";
		}

		// 同名的旧纹理必须作废，否则桌面上那张卡还画着旧图 ——
		// "改了图却没变"是最容易让人以为功能坏了的症状。
		TextureStore.Invalidate(target);
		ImportCount++;
		return fileName;
	}

	/// <summary>
	/// 文件名 → 完整路径。三种输入都收：
	/// <list type="bullet">
	/// <item>空串 → 空串（调用方据此判断"没设图"）</item>
	/// <item>已经带目录的路径（<c>res://</c> / <c>user://</c> / 盘符）→ 原样返回，不硬塞进 images/</item>
	/// <item>裸文件名 → <c>&lt;saveRoot&gt;/saves/&lt;当前存档&gt;/images/&lt;文件名&gt;</c></item>
	/// </list>
	/// </summary>
	public static string ResolvePath(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
			return "";

		string value = raw.Replace('\\', '/').Trim();

		if (value.Contains("://") || value.Contains('/') || value.Contains(':'))
			return value;

		return AppPaths.ImageFile(AppPaths.CurrentSave, value);
	}

	/// <summary>列出当前存档 <c>images/</c> 里的全部图片文件名（按名排序，给编辑器做下拉用）。</summary>
	public static string[] ListImages(string saveName)
	{
		string dir = AppPaths.ImagesDir(saveName);
		if (!DirAccess.DirExistsAbsolute(dir))
			return System.Array.Empty<string>();

		var names = new System.Collections.Generic.List<string>();
		using DirAccess? d = DirAccess.Open(dir);
		if (d is null)
			return System.Array.Empty<string>();

		foreach (string entry in d.GetFiles())
		{
			if (IsAllowed(ExtensionOf(entry)))
				names.Add(entry);
		}

		names.Sort(System.StringComparer.OrdinalIgnoreCase);
		return names.ToArray();
	}

	// ------------------------------------------------------------------ 文件名

	/// <summary>取文件名部分（去掉目录），并把非法字符换掉。</summary>
	internal static string BaseNameOf(string path)
	{
		int slash = path.LastIndexOf('/');
		string name = slash >= 0 ? path[(slash + 1)..] : path;
		return Sanitize(name);
	}

	internal static string ExtensionOf(string path)
	{
		int dot = path.LastIndexOf('.');
		int slash = path.LastIndexOf('/');
		if (dot <= slash || dot < 0)
			return "";

		return path[dot..].ToLowerInvariant();
	}

	private static bool IsAllowed(string extension)
	{
		foreach (string allowed in AllowedExtensions)
		{
			if (extension == allowed)
				return true;
		}

		return false;
	}

	/// <summary>把文件名收拾成合法的磁盘名。空名字会退回 <c>image</c>。</summary>
	private static string Sanitize(string name)
	{
		var sb = new System.Text.StringBuilder(name.Length);
		foreach (char c in name)
		{
			if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || c < ' ')
				sb.Append('_');
			else
				sb.Append(c);
		}

		string cleaned = sb.ToString().Trim().TrimStart('.').Trim();
		return cleaned.Length == 0 ? "image" : cleaned;
	}

	/// <summary>
	/// 找一个不冲突的文件名。
	/// 已有同名且<b>内容相同</b> → 直接复用（反复导入同一张图不会堆副本）；
	/// 已有同名但内容不同 → 加 <c>_2</c> / <c>_3</c> 后缀，绝不动原来那个文件。
	/// </summary>
	private static string AllocateName(string saveName, string baseName, byte[] bytes)
	{
		string extension = ExtensionOf(baseName);
		string stem = extension.Length > 0 ? baseName[..^extension.Length] : baseName;

		for (int suffix = 1; suffix < 1000; suffix++)
		{
			string candidate = suffix == 1
				? $"{stem}{extension}"
				: $"{stem}_{suffix}{extension}";

			string path = AppPaths.ImageFile(saveName, candidate);
			if (!FileAccess.FileExists(path))
				return candidate;

			byte[]? existing = FileAccess.GetFileAsBytes(path);
			if (existing is not null && SameBytes(existing, bytes))
				return candidate;   // 同一张图：复用
		}

		return "";
	}

	private static bool SameBytes(byte[] a, byte[] b)
	{
		if (a.Length != b.Length)
			return false;

		for (int i = 0; i < a.Length; i++)
		{
			if (a[i] != b[i])
				return false;
		}

		return true;
	}
}
