﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using AutoPBW;
using AutoPBW.WPF.Properties;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;

namespace AutoPBW.WPF
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		private Timer refreshTimer;
		private TaskbarIcon taskbarIcon;

		/// <summary>
		/// System process for current turn processing.
		/// </summary>
		private Process currentTurnProcess = null;

		/// <summary>
		/// Current game being processed.
		/// </summary>
		private HostGame currentTurnGame = null;

		/// <summary>
		/// Current turn processing exit code.
		/// </summary>
		private int? currentTurnExitCode = null;

		public MainWindow()
		{
			InitializeComponent();

			// TODO - get refresh time from PBW
			refreshTimer = new Timer(1000 * 60 * 2); // 2 minute refresh by default
			refreshTimer.Elapsed += refreshTimer_Elapsed;

			taskbarIcon = new TaskbarIcon();
			taskbarIcon.IconSource = Icon;
			taskbarIcon.TrayMouseDoubleClick += taskbarIcon_TrayMouseDoubleClick;
			taskbarIcon.ContextMenu = new ContextMenu();
			var miExit = new MenuItem();
			miExit.Header = "Exit AutoPBW";
			miExit.Click += miExit_Click;
			taskbarIcon.ContextMenu.Items.Add(miExit);
		}

		void miExit_Click(object sender, RoutedEventArgs e)
		{
			exiting = true;
			Close();
		}

		void taskbarIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
		{
			Show();
		}

		void refreshTimer_Elapsed(object sender, ElapsedEventArgs e)
		{
			Dispatcher.Invoke(() => RefreshData());
		}

		private void window_Loaded(object sender, RoutedEventArgs e)
		{
			txtUsername.Text = Config.Instance.Username;
			txtPassword.Password = Config.Instance.Password;

			if (string.IsNullOrWhiteSpace(Config.Instance.Username) || string.IsNullOrWhiteSpace(Config.Instance.Password))
			{
				// let the user enter username/password
				tabSettings.Focus();
			}
			else
			{
				// load games list
				Login();
				RefreshData();
			}
		}

		private void RefreshData()
		{
			// set cursor
			Cursor = Cursors.Wait;

			// remember selection
			var selGame = gridPlayerGames.SelectedItem as PlayerGame;
			var selCode = selGame == null ? null : selGame.Code;
			IEnumerable<HostGame> hostGames = null;

			try
			{
				// load host games
				{
					var hostGameViewSource = ((CollectionViewSource)(this.FindResource("hostGameViewSource")));
					hostGames = PBW.GetHostGames().ToArray();
					hostGameViewSource.Source = hostGames;
				}

				// load player games
				{
					var playerGameViewSource = ((CollectionViewSource)(this.FindResource("playerGameViewSource")));
					var oldGames = (IEnumerable<PlayerGame>)playerGameViewSource.Source;
					if (oldGames == null)
						oldGames = Enumerable.Empty<PlayerGame>();
					var newGames = PBW.GetPlayerGames().ToArray();
					playerGameViewSource.Source = newGames;
					var newReady = new HashSet<PlayerGame>();
					var waiting = newGames.Where(g => g.Status == PlayerStatus.Waiting);
					var waitingPLR = waiting.Where(g => g.TurnNumber > 0);
					var waitingEMP = waiting.Where(g => g.TurnNumber == 0);
					foreach (var ng in waiting)
					{
						var og = oldGames.SingleOrDefault(g => g.Code == ng.Code);
						if (og == null || og.Status != PlayerStatus.Waiting)
							newReady.Add(ng);
					}

					// newly ready games, show a popup
					if (waitingPLR.Intersect(newReady).Count() > 1)
						taskbarIcon.ShowBalloonTip("New turns ready", waitingPLR.Intersect(newReady).Count() + " new games are ready to play.", BalloonIcon.None);
					else if (waitingPLR.Intersect(newReady).Count() == 1)
						taskbarIcon.ShowBalloonTip("New turn ready", waitingPLR.Intersect(newReady).Single() + " is ready to play.", BalloonIcon.None);
					else if (waitingEMP.Intersect(newReady).Count() > 1)
						taskbarIcon.ShowBalloonTip("Awaiting empires", waitingEMP.Intersect(newReady).Count() + " games are awaiting empire setup files.", BalloonIcon.None);
					else if (waitingEMP.Intersect(newReady).Count() == 1)
						taskbarIcon.ShowBalloonTip("Awaiting empire", waitingEMP.Intersect(newReady).Single() + " is awaiting an empire setup file.", BalloonIcon.None);

					// all ready games, set a tooltip
					if (waitingPLR.Count() > 1)
						taskbarIcon.ToolTipText = waitingPLR.Count() + " games are ready to play.";
					else if (waitingPLR.Count() == 1)
						taskbarIcon.ToolTipText = waitingPLR.Single() + " is ready to play.";
					else if (waitingEMP.Count() > 1)
						taskbarIcon.ToolTipText = waitingEMP.Count() + " games are awaiting empire setup files.";
					else if (waitingEMP.Count() == 1)
						taskbarIcon.ToolTipText = waitingEMP.Single() + " is awaiting an empire setup file.";
					else
						taskbarIcon.ToolTipText = "All caught up!";
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show("Unable to refresh games lists: " + ex.Message);
			}
			try
			{
				// load engines
				var engineViewSource = ((CollectionViewSource)(this.FindResource("engineViewSource")));
				engineViewSource.Source = Config.Instance.Engines;
				lstEngines.GetBindingExpression(ListView.ItemsSourceProperty).UpdateTarget();
				ddlEngine.GetBindingExpression(ComboBox.ItemsSourceProperty).UpdateTarget();

				// load mods
				var modViewSource = ((CollectionViewSource)(this.FindResource("modViewSource")));
				modViewSource.Source = Config.Instance.Mods;
				lstMods.GetBindingExpression(ListView.ItemsSourceProperty).UpdateTarget();
			}
			catch (Exception ex)
			{
				MessageBox.Show("Unable to refresh engines/mods lists: " + ex.Message);
			}

			// load log
			lstLog.DataContext = PBW.Log.ReadAll();

			// process turn if needed
			if (hostGames != null)
			{
				var gamesToProcess = hostGames.Where(g => g.Status == HostStatus.PlayersReady).ToList();
				if (currentTurnProcess == null)
				{
					while (gamesToProcess.Any())
					{
						var game = gamesToProcess.First();
						if (CheckModAndEngine(game))
						{
							taskbarIcon.ShowBalloonTip("Processing turn", "Processing turn for " + game + ".", BalloonIcon.None);
							try
							{
								game.DownloadTurns();
								currentTurnGame = game;
								currentTurnProcess = new Process();
								currentTurnProcess.StartInfo = game.ProcessTurnPrepare();
								currentTurnProcess.EnableRaisingEvents = true;
								currentTurnProcess.Exited += process_Exited;
								currentTurnProcess.Start();
								break; // wait till we finish this one
							}
							catch (Exception ex)
							{
								taskbarIcon.ShowBalloonTip("Turn processing failed", "Turn processing for " + game + " failed:\n" + ex.Message, BalloonIcon.None);
								game.PlaceHold(ex.Message);
								if (currentTurnProcess != null)
								{
									currentTurnProcess.Close();
									currentTurnProcess = null;
									currentTurnGame = null;
									currentTurnExitCode = null;
								}
								gamesToProcess.Remove(game);
							}
						}
						else
							gamesToProcess.Remove(game); // can't process this game now
					}
				}
			}

			// remember selection
			var newGame = gridPlayerGames.Items.Cast<PlayerGame>().SingleOrDefault(g => g.Code == selCode);
			gridPlayerGames.SelectedItem = newGame;

			// start a timer so we can refresh in a few minutes
			refreshTimer.Start();

			// reset cursor
			Cursor = Cursors.Arrow;
		}

		void process_Exited(object sender, EventArgs e)
		{
			var p = currentTurnProcess;
			var g = currentTurnGame;
			currentTurnProcess = null;
			currentTurnGame = null;
			currentTurnExitCode = p.ExitCode;
			if (currentTurnExitCode.Value == 0)
			{
				try
				{
					Dispatcher.Invoke(() => { g.UploadTurn(); });
				}
				catch (Exception ex)
				{
					Dispatcher.Invoke(() => { taskbarIcon.ShowBalloonTip("Error uploading turn", "Unable to upload new turn for hosted game {0}: {1}".F(g, ex.Message), BalloonIcon.Error); });
				}
				Dispatcher.Invoke(() => { RefreshData(); });
				currentTurnExitCode = null;
			}
			else
			{
				Dispatcher.Invoke(() => taskbarIcon.ShowBalloonTip("Turn processing failed", "Turn processing for {0} failed with exit code {1}.".F(g, currentTurnExitCode), BalloonIcon.Error));
				try
				{
					g.PlaceHold("{0} exited with error code {1}".F(System.IO.Path.GetFileName(p.StartInfo.FileName), currentTurnExitCode));
				}
				catch (Exception ex)
				{
					Dispatcher.Invoke(() => { taskbarIcon.ShowBalloonTip("Error placing hold", "Unable to place processing hold on {0}: {1}".F(g, ex.Message), BalloonIcon.Error); });
				}
			}
		}

		private void Login()
		{
			try
			{
				PBW.Login(Config.Instance.Username, Config.Instance.Password);
			}
			catch (Exception ex)
			{
				MessageBox.Show("Unable to log in to PBW: " + ex.Message);
			}
		}

		private void btnSaveSettings_Click(object sender, RoutedEventArgs e)
		{
			Config.Instance.Username = txtUsername.Text;
			Config.Instance.Password = txtPassword.Password;
			Config.Save();
			Login();
			RefreshData();
			tabPlayerGames.Focus();
		}

		private void playerGameDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
		{

		}

		private T GetAncestor<T>(FrameworkElement source) where T : FrameworkElement
		{
			while (!(source is T || source == null))
				source = (FrameworkElement)VisualTreeHelper.GetParent(source);
			return (T)source;
		}

		private void lstEngines_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			var engine = lstEngines.SelectedItem as Engine;
			gridEngineDetails.DataContext = engine;
		}

		private void btnSaveEngine_Click(object sender, RoutedEventArgs e)
		{
			var engine = lstEngines.SelectedItem as Engine;
			if (engine != null)
			{
				engine.Code = engineCodeTextBox.Text;
				engine.HostArguments = hostArgumentsTextBox.Text;
				engine.HostExecutable = hostExecutableTextBox.Text;
				engine.HostTurnUploadFilter = hostTurnUploadFilterTextBox.Text;
				engine.IsUnknown = false;
				engine.PlayerArguments = playerArgumentsTextBox.Text;
				engine.PlayerExecutable = playerExecutableTextBox.Text;
				engine.PlayerTurnUploadFilter = playerTurnUploadFilterTextBox.Text;
				Config.Save();
				RefreshData();
				lstEngines.SelectedItem = null;
			}
		}

		private void btnDeleteEngine_Click(object sender, RoutedEventArgs e)
		{
			var engine = lstEngines.SelectedItem as Engine;
			if (engine != null)
			{
				if (Config.Instance.Mods.Any(m => m.Engine == engine))
					MessageBox.Show("You cannot delete an engine which has mods.");
				else if (MessageBox.Show("Really delete the configuration for " + engine + "?", "Confirm Delete", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
				{
					Config.Instance.Engines.Remove(engine);
					Config.Save();
					RefreshData();
					lstEngines.SelectedItem = null;
				}
			}
		}

		private void btnAddEngine_Click(object sender, RoutedEventArgs e)
		{
			var engine = Engine.Find("New Engine");
			RefreshData();
			lstEngines.SelectedItem = engine;
		}

		private void btnAddMod_Click(object sender, RoutedEventArgs e)
		{
			var mod = Mod.Find("New Mod");
			RefreshData();
			lstMods.SelectedItem = mod;
		}

		private void btnDeleteMod_Click(object sender, RoutedEventArgs e)
		{
			var mod = lstMods.SelectedItem as Mod;
			if (mod != null)
			{
				if (MessageBox.Show("Really delete the configuration for " + mod + "?", "Confirm Delete", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
				{
					Config.Instance.Mods.Remove(mod);
					Config.Save();
					RefreshData();
					lstMods.SelectedItem = null;
				}
			}
		}

		private void btnSaveMod_Click(object sender, RoutedEventArgs e)
		{
			var mod = lstMods.SelectedItem as Mod;
			if (mod != null)
			{
				mod.Code = modCodeTextBox.Text;
				mod.Engine = ddlEngine.SelectedItem as Engine;
				mod.Path = modPathTextBox.Text;
				mod.SavePath = savePathTextBox.Text;
				mod.EmpirePath = empirePathTextBox.Text;
				mod.IsUnknown = false;
				Config.Save();
				RefreshData();
				lstMods.SelectedItem = null;
			}
		}

		private void lstMods_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			var mod = lstMods.SelectedItem as Mod;
			gridModDetails.DataContext = mod;
		}

		private void btnDownload_Click(object sender, RoutedEventArgs e)
		{
			var game = (PlayerGame)gridPlayerGames.SelectedItem;
			if (CheckModAndEngine(game))
			{
				Cursor = Cursors.Wait;
				if (game.TurnNumber == 0)
				{
					// TODO - download GSU?
					MessageBox.Show("Nothing to download on turn zero.");
				}
				else
				{
					try
					{
						game.DownloadTurn();
						Cursor = Cursors.Arrow;
						if (MessageBox.Show("Turn downloaded. Play it now?", "Turn Ready", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
						{
							try
							{
								game.PlayTurn();
							}
							catch (Exception ex)
							{
								MessageBox.Show("Could not play turn: " + ex.Message + ".");
							}
						}
					}
					catch (Exception ex)
					{
						MessageBox.Show("Could not download turn: " + ex.Message + ".");
					}
				}
				Cursor = Cursors.Arrow;
			}
		}

		private void btnPlay_Click(object sender, RoutedEventArgs e)
		{
			var game = (PlayerGame)gridPlayerGames.SelectedItem;
			if (CheckModAndEngine(game))
			{
				Cursor = Cursors.Wait;
				if (game.TurnNumber == 0)
				{
					// create EMP
					try
					{
						game.CreateEmpire();
					}
					catch (Exception ex)
					{
						MessageBox.Show("Could not create empire setup: " + ex.Message + ".");
					}
				}
				else
				{
					// play turn
					try
					{
						game.PlayTurn();
					}
					catch (Exception ex)
					{
						MessageBox.Show("Could not play turn: " + ex.Message + ".");
					}
				}
				Cursor = Cursors.Arrow;
			}
		}

		private void btnUpload_Click(object sender, RoutedEventArgs e)
		{
			var game = (PlayerGame)gridPlayerGames.SelectedItem;
			if (CheckModAndEngine(game))
			{
				Cursor = Cursors.Wait;
				if (game.TurnNumber == 0)
				{
					// upload EMP
					try
					{
						var dlg = new OpenFileDialog();
						dlg.InitialDirectory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(game.Engine.PlayerExecutable.Trim('"')), game.Mod.EmpirePath);
						var result = dlg.ShowDialog();
						if (result.HasValue && result.Value)
							game.UploadEmpire(dlg.FileName);
					}
					catch (Exception ex)
					{
						MessageBox.Show("Could not upload turn: " + ex.Message + ".");
					}
				}
				else
				{
					// upload PLR
					try
					{
						game.UploadTurn();
					}
					catch (Exception ex)
					{
						MessageBox.Show("Could not upload turn: " + ex.Message + ".");
					}
				}
				RefreshData();
				Cursor = Cursors.Arrow;

			}
		}

		private bool CheckModAndEngine(PlayerGame game)
		{
			if (game == null)
			{
				MessageBox.Show("No game is selected.");
				return false;
			}
			else if (game.Engine == null)
			{
				MessageBox.Show("Mod " + game.Mod + " does not have an engine assigned to it. Please configure it.");
				lstMods.SelectedItem = lstMods.Items.Cast<Mod>().SingleOrDefault(m => m.Code == game.Mod.Code);
				tabMods.Focus();
				return false;
			}
			else if (game.Engine.IsUnknown)
			{
				MessageBox.Show("Unknown game engine " + game.Engine + ". Please configure it.");
				lstEngines.SelectedItem = lstEngines.Items.Cast<Engine>().SingleOrDefault(e => e.Code == game.Engine.Code);
				tabEngines.Focus();
				return false;
			}
			else if (game.Mod.IsUnknown)
			{
				MessageBox.Show("Unknown mod " + game.Mod + " for " + game.Engine + ". Please configure it.");
				lstMods.SelectedItem = lstMods.Items.Cast<Mod>().SingleOrDefault(m => m.Code == game.Mod.Code);
				tabMods.Focus();
				return false;
			}
			else
				return true;
		}

		private bool CheckModAndEngine(HostGame game)
		{
			if (game == null)
				return true;
			else if (game.Engine == null)
			{
				taskbarIcon.ShowBalloonTip("Configuration required", "Mod " + game.Mod + " required by hosted game " + game + " has no engine assigned. Please configure it.", BalloonIcon.None);
				return false;
			}
			else if (game.Engine.IsUnknown)
			{
				taskbarIcon.ShowBalloonTip("Configuration required", "Unknown game engine " + game.Engine + " required by hosted game " + game + ". Please configure it.", BalloonIcon.None);
				return false;
			}
			else if (game.Mod.IsUnknown)
			{
				taskbarIcon.ShowBalloonTip("Configuration required", "Unknown mod " + game.Mod + " required by hosted game " + game + ". Please configure it.", BalloonIcon.None);
				return false;
			}
			else
				return true;

		}

		private void window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
		{
			if (!exiting)
			{
				// just hide the window, we have a taskbar icon
				e.Cancel = true;
				Hide();
			}
			else
				taskbarIcon.Dispose(); // get rid of the icon
		}

		private bool exiting;

		private void btnRefresh_Click(object sender, RoutedEventArgs e)
		{
			RefreshData();
		}

		private void btnWebsite_Click(object sender, RoutedEventArgs e)
		{
			Process.Start("http://pbw.spaceempires.net/dashboard");
		}

		private void btnExit_Click(object sender, RoutedEventArgs e)
		{
			exiting = true;
			Close();
		}

		private void btnHelp_Click(object sender, RoutedEventArgs e)
		{
			Process.Start("AutoPBW-Manual.html");
		}
	}
}

