using System.Globalization;
using Godot;

namespace TabletopSimulator.Dev;

/// <summary>
/// 读取命令行参数（<c>--</c> 之后的部分）。
/// 例：<c>godot --path . -- --shot "D:/tmp/a.png" --shot-frames 30 --shot-exit</c>
/// </summary>
internal static class CmdLine
{
	private static string[] Args => OS.GetCmdlineUserArgs();

	internal static bool HasFlag(string flag)
	{
		foreach (string a in Args)
		{
			if (string.Equals(a, flag, System.StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}

	/// <summary>取 <c>--key value</c> 形式的值；没有则返回空串。</summary>
	internal static string GetValue(string key)
	{
		string[] args = Args;
		for (int i = 0; i < args.Length - 1; i++)
		{
			if (string.Equals(args[i], key, System.StringComparison.OrdinalIgnoreCase))
				return args[i + 1];
		}

		return string.Empty;
	}

	internal static int GetInt(string key, int fallback)
	{
		string raw = GetValue(key);
		return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
			? v
			: fallback;
	}
}
