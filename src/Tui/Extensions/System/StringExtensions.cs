namespace Tui.Extensions.System;

internal static class StringExtensions
{
    extension(string text)
    {
        public Stream ToStream()
        {
            byte[] bytes = global::System.Text.Encoding.UTF8.GetBytes(text);
            return new MemoryStream(bytes, writable: false);
        }
    }
}
