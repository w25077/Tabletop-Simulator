using System.Collections.Generic;
using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 图片纹理的加载与缓存。
///
/// 关键点：<b><c>user://</c> 目录下的 PNG 不走 Godot 的导入系统</b>，
/// 因此不能 <c>ResourceLoader.Load</c>，必须用 <see cref="Image.LoadFromFile"/>
/// 手动读成 <see cref="Image"/> 再包成 <see cref="ImageTexture"/>。
///
/// 这正好带来一个好处：改图立即生效，不需要重新导入，也不需要重启编辑器 ——
/// 对「边调卡面边试玩」的玩法验证流程很关键。
/// </summary>
public static class TextureStore
{
	/// <summary>缓存。<c>null</c> 也缓存，避免每次绘制都去戳一次不存在的文件。</summary>
	private static readonly Dictionary<string, Texture2D?> Cache = new();

	/// <summary>按路径取纹理；路径为空或文件不存在返回 <c>null</c>。</summary>
	public static Texture2D? Get(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;

		if (Cache.TryGetValue(path, out Texture2D? cached))
			return cached;

		Texture2D? texture = Load(path);
		Cache[path] = texture;
		return texture;
	}

	private static Texture2D? Load(string path)
	{
		if (!FileAccess.FileExists(path))
		{
			GD.PushWarning($"[TextureStore] 图片不存在：{path}");
			return null;
		}

		Image? image = Image.LoadFromFile(path);
		if (image is null)
		{
			GD.PushWarning($"[TextureStore] 图片解码失败：{path}");
			return null;
		}

		return ImageTexture.CreateFromImage(image);
	}

	/// <summary>丢弃某个路径的缓存（改图后调用）。</summary>
	public static void Invalidate(string path) => Cache.Remove(path);

	/// <summary>清空全部缓存（切换存档时调用）。</summary>
	public static void Clear() => Cache.Clear();

	/// <summary>当前缓存条目数（自检报告用）。</summary>
	public static int CachedCount => Cache.Count;
}
