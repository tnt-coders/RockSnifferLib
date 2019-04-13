﻿using RockSnifferLib.Logging;
using RockSnifferLib.Sniffing;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace RockSnifferLib.Cache
{
    public class SQLiteCache : ICache
    {
        private SQLiteConnection Connection { get; set; }

        public SQLiteCache()
        {
            if (!File.Exists("cache.sqlite"))
            {
                SQLiteConnection.CreateFile("cache.sqlite");
            }

            Connection = new SQLiteConnection("Data Source=cache.sqlite;");
            Connection.Open();

            CreateTables();
        }

        private void CreateTables()
        {
            var q = @"
            CREATE TABLE IF NOT EXISTS `songs` (
	            `id`	INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
	            `psarcFile`	TEXT NOT NULL,
	            `songid`	TEXT NOT NULL,
	            `songname`	TEXT NOT NULL,
	            `artistname`	TEXT NOT NULL,
	            `albumname`	TEXT,
	            `songLength`	REAL,
	            `albumYear`	INTEGER,
	            `numArrangements`	INTEGER,
                `album_art`	BLOB,
	            `toolkit_version`	TEXT,
	            `toolkit_author`	TEXT,
	            `toolkit_package_version`	TEXT,
	            `toolkit_comment`	TEXT
            );";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;
                cmd.ExecuteNonQuery();
            }

            q = "CREATE INDEX IF NOT EXISTS `filepath` ON `songs` (`psarcFile` );";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;
                cmd.ExecuteNonQuery();
            }

            q = "CREATE INDEX IF NOT EXISTS`songid` ON `songs` (`songid` );";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;
                cmd.ExecuteNonQuery();
            }

            if (Logger.logCache)
            {
                Logger.Log("SQLite database initialised");
            }
        }

        public void Add(string filepath, Dictionary<string, SongDetails> allDetails)
        {
            var q = @"
            INSERT INTO `songs`(
                `psarcFile`,
                `songid`,
                `songname`,
                `artistname`,
                `albumname`,
                `songLength`,
                `albumYear`,
                `numArrangements`,
                `album_art`,
                `toolkit_version`,
                `toolkit_author`,
                `toolkit_package_version`,
                `toolkit_comment`
            )
            VALUES (@psarcFile,@songid,@songname,@artistname,@albumname,@songLength,@albumYear,@numArrangements,@album_art,@toolkit_version,@toolkit_author,@toolkit_package_version,@toolkit_comment);
            ";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;

                cmd.Parameters.Add("@psarcFile", System.Data.DbType.String);
                cmd.Parameters.Add("@songid", System.Data.DbType.String);
                cmd.Parameters.Add("@songname", System.Data.DbType.String);
                cmd.Parameters.Add("@artistname", System.Data.DbType.String);
                cmd.Parameters.Add("@albumname", System.Data.DbType.String);
                cmd.Parameters.Add("@songLength", System.Data.DbType.Single);
                cmd.Parameters.Add("@albumYear", System.Data.DbType.Int32);
                cmd.Parameters.Add("@numArrangements", System.Data.DbType.Int32);
                cmd.Parameters.Add("@album_art", System.Data.DbType.Binary);
                cmd.Parameters.Add("@toolkit_version", System.Data.DbType.String);
                cmd.Parameters.Add("@toolkit_author", System.Data.DbType.String);
                cmd.Parameters.Add("@toolkit_package_version", System.Data.DbType.String);
                cmd.Parameters.Add("@toolkit_comment", System.Data.DbType.String);

                foreach (KeyValuePair<string, SongDetails> pair in allDetails)
                {
                    var sd = pair.Value;

                    cmd.Parameters["@psarcFile"].Value = filepath;
                    cmd.Parameters["@songid"].Value = sd.songID;
                    cmd.Parameters["@songname"].Value = sd.songName;
                    cmd.Parameters["@artistname"].Value = sd.artistName;
                    cmd.Parameters["@albumname"].Value = sd.albumName;
                    cmd.Parameters["@songLength"].Value = sd.songLength;
                    cmd.Parameters["@albumYear"].Value = sd.albumYear;
                    cmd.Parameters["@numArrangements"].Value = sd.numArrangements;
                    cmd.Parameters["@toolkit_version"].Value = sd.toolkit.version;
                    cmd.Parameters["@toolkit_author"].Value = sd.toolkit.author;
                    cmd.Parameters["@toolkit_package_version"].Value = sd.toolkit.package_version;
                    cmd.Parameters["@toolkit_comment"].Value = sd.toolkit.comment;
                    cmd.Parameters["@album_art"].Value = null;

                    if (sd.albumArt != null)
                    {
                        using (var ms = new MemoryStream())
                        {
                            sd.albumArt.Save(ms, ImageFormat.Png);

                            cmd.Parameters["@album_art"].Value = ms.ToArray();
                        }
                    }

                    cmd.ExecuteNonQuery();

                    if (Logger.logCache)
                    {
                        Logger.Log("Cached {0}/{1}", Path.GetFileName(filepath), sd.songID);
                    }
                }
            }
        }

        public bool Contains(string filepath)
        {
            string q = @"SELECT EXISTS(SELECT 1 FROM songs WHERE psarcFile = @psarcFile)";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;
                cmd.Parameters.AddWithValue("@psarcFile", filepath);

                var result = cmd.ExecuteScalar();
                return (long)result == 1;
            }
        }

        public SongDetails Get(string filepath, string songID)
        {
            return Get(songID);
        }

        public SongDetails Get(string SongID)
        {
            string q = @"SELECT * FROM songs WHERE songid = @songid LIMIT 1";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;
                cmd.Parameters.AddWithValue("@songid", SongID);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var sd = new SongDetails
                        {
                            songID = reader.GetString(reader.GetOrdinal("songid")),
                            songName = reader.GetString(reader.GetOrdinal("songname")),
                            artistName = reader.GetString(reader.GetOrdinal("artistname")),
                            albumName = reader.GetString(reader.GetOrdinal("albumname")),
                            songLength = reader.GetFloat(reader.GetOrdinal("songLength")),
                            albumYear = reader.GetInt32(reader.GetOrdinal("albumYear")),
                            numArrangements = reader.GetInt32(reader.GetOrdinal("numArrangements")),
                            albumArt = null,

                            toolkit = new ToolkitDetails
                            {
                                version = ReadField<string>(reader, "toolkit_version"),
                                author = ReadField<string>(reader, "toolkit_author"),
                                package_version = ReadField<string>(reader, "toolkit_package_version"),
                                comment = ReadField<string>(reader, "toolkit_comment")
                            }
                        };

                        try
                        {

                            var blob = ReadField<byte[]>(reader, "album_art");

                            using (var ms = new MemoryStream(blob))
                            {
                                sd.albumArt = Image.FromStream(ms);
                            }
                        }
                        catch
                        {

                        }

                        return sd;
                    }
                }
            }

            return null;
        }

        private T ReadField<T>(SQLiteDataReader reader, string field)
        {
            int ordinal = reader.GetOrdinal(field);

            if (reader.IsDBNull(ordinal))
            {
                return default(T);
            }

            return (T)reader.GetValue(ordinal);
        }
    }
}
