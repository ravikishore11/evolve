﻿using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Evolve.Utilities;

namespace Evolve.Migration
{
    public class FileMigrationScript : MigrationScript
    {
        private const string IncorrectMigrationChecksum = "Validate failed: invalid checksum for migration: {0}.";

        public FileMigrationScript(string path, string version, string description, Encoding textEncoding = null)
            : base(version,
                   description,
                   System.IO.Path.GetFileName(Check.FileExists(path, nameof(path))),
                   File.ReadAllText(path, textEncoding))
        {
            Path = path;
            Encoding = textEncoding ?? Encoding.UTF8;
        }
        
        public string Path { get; }

        public Encoding Encoding { get; }

        public override void ValidateChecksum(string checksum)
        {
            Check.NotNull(checksum, nameof(checksum));

            try
            {
                base.ValidateChecksum(checksum);
            }
            catch (Exception ex)
            {
                if (checksum != FallbackCheck())
                {
                    throw ex;
                }
            }
        }

        /// <summary>
        ///     Calculate the checksum with the pre v1.8.0 version.
        /// </summary>
        private string FallbackCheck()
        {
            using (var md5 = MD5.Create())
            {
                using (FileStream stream = File.OpenRead(Path))
                {
                    byte[] checksum = md5.ComputeHash(stream);
                    return BitConverter.ToString(checksum).Replace("-", string.Empty);
                }
            }
        }
    }
}
