using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FlipPix.UI.Services
{
    public class ChunkPromptCacheService : IDisposable
    {
        private readonly string _dbPath;
        private bool _disposed;

        public ChunkPromptCacheService()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix");
            Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, "chunk_prompts.db");
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS chunk_prompts (
                    video_key   TEXT    NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    prompt      TEXT    NOT NULL,
                    created_at  TEXT    NOT NULL,
                    PRIMARY KEY (video_key, chunk_index)
                );
                CREATE TABLE IF NOT EXISTS chunk_motion (
                    video_key   TEXT    NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    motion      TEXT    NOT NULL,
                    created_at  TEXT    NOT NULL,
                    PRIMARY KEY (video_key, chunk_index)
                );
                CREATE TABLE IF NOT EXISTS image_appearance (
                    image_key   TEXT    NOT NULL,
                    appearance  TEXT    NOT NULL,
                    created_at  TEXT    NOT NULL,
                    PRIMARY KEY (image_key)
                )
                """;
            cmd.ExecuteNonQuery();
        }

        // ── Combined prompts (legacy / backward compat) ───────────────────────

        public Dictionary<int, string> GetAllPrompts(string videoPath)
        {
            var key = MakeKey(videoPath);
            var result = new Dictionary<int, string>();
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT chunk_index, prompt FROM chunk_prompts WHERE video_key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result[reader.GetInt32(0)] = reader.GetString(1);
            }
            catch { }
            return result;
        }

        public void SavePrompt(string videoPath, int chunkIndex, string prompt)
        {
            var key = MakeKey(videoPath);
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO chunk_prompts (video_key, chunk_index, prompt, created_at)
                    VALUES (@key, @idx, @prompt, @ts)
                    """;
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@idx", chunkIndex);
                cmd.Parameters.AddWithValue("@prompt", prompt);
                cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        public void DeleteAllForVideo(string videoPath)
        {
            var key = MakeKey(videoPath);
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM chunk_prompts WHERE video_key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // ── Per-chunk motion (video-specific, image-independent) ─────────────

        public Dictionary<int, string> GetAllMotion(string videoPath)
        {
            var key = MakeKey(videoPath);
            var result = new Dictionary<int, string>();
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT chunk_index, motion FROM chunk_motion WHERE video_key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result[reader.GetInt32(0)] = reader.GetString(1);
            }
            catch { }
            return result;
        }

        public void SaveMotion(string videoPath, int chunkIndex, string motion)
        {
            var key = MakeKey(videoPath);
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO chunk_motion (video_key, chunk_index, motion, created_at)
                    VALUES (@key, @idx, @motion, @ts)
                    """;
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@idx", chunkIndex);
                cmd.Parameters.AddWithValue("@motion", motion);
                cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        public void DeleteAllMotionForVideo(string videoPath)
        {
            var key = MakeKey(videoPath);
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM chunk_motion WHERE video_key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // ── Per-image appearance (image-specific, video-independent) ─────────

        public string? GetAppearance(string imagePath)
        {
            var key = MakeKey(imagePath);
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT appearance FROM image_appearance WHERE image_key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                var result = cmd.ExecuteScalar();
                return result as string;
            }
            catch { return null; }
        }

        public void SaveAppearance(string imagePath, string appearance)
        {
            var key = MakeKey(imagePath);
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO image_appearance (image_key, appearance, created_at)
                    VALUES (@key, @appearance, @ts)
                    """;
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@appearance", appearance);
                cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            return conn;
        }

        private static string MakeKey(string filePath)
        {
            var fi = new FileInfo(filePath);
            var raw = $"{filePath.ToLowerInvariant()}|{(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0L)}";
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}
