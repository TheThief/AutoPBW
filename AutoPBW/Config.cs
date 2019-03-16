﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SevenZip;

namespace AutoPBW
{
	/// <summary>
	/// Configuration for AutoPBW
	/// </summary>
	public class Config
	{
		static Config()
		{
			Instance = new Config();

			JsonSettings = new JsonSerializerSettings();
			JsonSettings.Formatting = Formatting.Indented;
			JsonSettings.PreserveReferencesHandling = PreserveReferencesHandling.All;

			if (IntPtr.Size == 8) // 64-bit
			{
				SevenZipCompressor.SetLibraryPath("7z64.dll");
				SevenZipExtractor.SetLibraryPath("7z64.dll");

			}
			else
			{
				SevenZipCompressor.SetLibraryPath("7z.dll");
				SevenZipExtractor.SetLibraryPath("7z.dll");
			}
		}

		private Config()
		{
			Engines = new ObservableCollection<Engine>();
			Mods = new ObservableCollection<Mod>();

			// set up some defaults
			var se4 = new Engine("SE4");
			se4.HostExecutable = @"C:\Autohost\SE4";
			se4.PlayerExecutable = @"C:\Games\SE4";
		}

		public static Config Instance { get; private set; }

		internal static Config Default { get; private set; }

		private const string filename = "AutoPBW.config.json";

		private const string defaultFilename = "AutoPBW.config.default.json";

		private static JsonSerializerSettings JsonSettings;

		public static void Load()
		{
			try
			{
				Default = JsonConvert.DeserializeObject<Config>(File.ReadAllText(defaultFilename), JsonSettings);
			}
			catch (Exception ex)
			{
				PBW.Log.Write("Could not load default config from " + filename + ".");
				PBW.Log.Write("Error that occurred: " + ex.Message);
				Default = new Config();
			}

			try
			{
				Instance = JsonConvert.DeserializeObject<Config>(File.ReadAllText(filename), JsonSettings);
			}
			catch (Exception ex)
			{
				PBW.Log.Write("Could not load config from " + filename + "; reverting to default settings.");
				PBW.Log.Write("Error that occurred: " + ex.Message);
				Instance = new Config();
				Instance.Username = Default.Username;
				Instance.Password = Default.Password;
				foreach (var e in Default.Engines)
					Instance.Engines.Add(e);
				foreach (var m in Default.Mods)
					Instance.Mods.Add(m);
			}
		}

		public static void Save()
		{
			File.WriteAllText(filename, JsonConvert.SerializeObject(Instance, JsonSettings));
		}

		/// <summary>
		/// User's PBW username.
		/// </summary>
		public string Username { get; set; }

		/// <summary>
		/// User's PBW password.
		/// </summary>
		public string Password { get; set; }

		/// <summary>
		/// Should this instance of AutoPBW host games?
		/// </summary>
		public bool EnableHosting { get; set; }

		/// <summary>
		/// Should games in which we are player zero (host player) be hidden?
		/// </summary>
		public bool HidePlayerZero { get; set; }

		/// <summary>
		/// Known game engines.
		/// </summary>
		public ObservableCollection<Engine> Engines { get; private set; }

		/// <summary>
		/// Known mods.
		/// </summary>
		public ObservableCollection<Mod> Mods { get; private set; }
	}
}
