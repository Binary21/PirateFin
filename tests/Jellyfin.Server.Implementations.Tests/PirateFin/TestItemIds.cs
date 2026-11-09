using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Stand-in for ILibraryManager.GetNewItemId: a stable id per (type, path), so tests can find generated items again.
/// </summary>
internal static class TestItemIds
{
    public static Guid For(string path, Type type)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(type.Name + path)).AsSpan(0, 16));
}
