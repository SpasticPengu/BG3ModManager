using AutoUpdaterDotNET;

using DivinityModManager.Util;
using DivinityModManager.Views;

using System.Text.RegularExpressions;
using System.Windows.Input;

namespace DivinityModManager.ViewModels;
public partial class AppUpdateWindowViewModel : ReactiveObject
{
	[Reactive] public bool IsVisible { get; set; }
	[Reactive] public bool CanConfirm { get; set; }
	[Reactive] public bool CanSkip { get; set; }
	[Reactive] public string AppTitle { get; set; }
	[Reactive] public Version AppVersion { get; set; }
	[Reactive] public string SkipButtonText { get; set; }
	[Reactive] public string UpdateDescription { get; set; }
	[Reactive] public string UpdateChangelogView { get; set; }
	[Reactive] public Version UpdateVersion { get; set; }

	public ICommand ConfirmCommand { get; private set; }
	public ICommand SkipCommand { get; private set; }
	public ReactiveCommand<UpdateInfoEventArgs, Unit> OnUpdateCheckCommand { get; private set; }

	[GeneratedRegex(@"^\s+$[\r\n]*", RegexOptions.Multiline)]
	private static partial Regex RemoveEmptyLinesRe();

	private static readonly Regex RemoveEmptyLinesPattern = RemoveEmptyLinesRe();

	private UpdateInfoEventArgs? _updateArgs;

	private void TryRunUpdate()
	{
		try
		{
			MainWindow.Self.ViewModel.Settings.LastUpdateCheck = DateTimeOffset.Now.ToUnixTimeSeconds();
			MainWindow.Self.ViewModel.SaveSettings();
			// True only once the installer has started (it waits for this process to exit). False when the download was
			// cancelled or failed, and the app must keep running then.
			if (AutoUpdater.DownloadUpdate(_updateArgs))
			{
				// A normal shutdown, not Environment.Exit, so the window's close handlers still run (window position, settings.json)
				System.Windows.Application.Current.Shutdown();
			}
			else
			{
				// The update window stays open, so the update can be tried again
				MainWindow.Self.ViewModel.ShowAlert("The update was cancelled or could not be downloaded", AlertType.Warning, 30);
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error updating program:\n{ex}");
			IsVisible = false;
		}
	}

	private bool _showAlert;

	private async Task OnUpdateCheckAsync(UpdateInfoEventArgs args)
	{
		try
		{
			// AutoUpdater raises CheckForUpdateEvent with only Error set when the update feed could not be downloaded or read
			if (args.Error != null)
			{
				DivinityApp.Log($"Error checking for update:\n{args.Error}");
				if (_showAlert) MainWindow.Self.ViewModel.ShowAlert($"Failed to check for updates: {args.Error.Message}", AlertType.Danger, 60);
				return;
			}

			var markdownText = await WebHelper.DownloadUrlAsStringAsync(DivinityApp.URL_CHANGELOG_RAW, CancellationToken.None);
			if (!String.IsNullOrEmpty(markdownText))
			{
				markdownText = RemoveEmptyLinesPattern.Replace(markdownText, string.Empty);
			}

			// The update window binds these properties, so set them on the UI thread
			await Observable.Start(() =>
			{
				if (!String.IsNullOrEmpty(markdownText)) UpdateChangelogView = markdownText;

				_updateArgs = args;

				if (args.IsUpdateAvailable)
				{
					UpdateDescription = $"{AppTitle} {args.CurrentVersion} is now available.\nYou have version {AppVersion} installed.";

					CanConfirm = true;
					SkipButtonText = "Skip";
					CanSkip = true;
					UpdateVersion = Version.Parse(args.CurrentVersion);
					if (_showAlert) MainWindow.Self.ViewModel.ShowAlert("Update found!", AlertType.Success, 20);
				}
				else
				{
					UpdateDescription = $"{AppTitle} is up-to-date.\nYou have version {AppVersion} installed.";
					CanConfirm = false;
					CanSkip = true;
					SkipButtonText = "Close";
					if (_showAlert) MainWindow.Self.ViewModel.ShowAlert("Already up-to-date", AlertType.Info, 20);
				}

				if (args.IsUpdateAvailable || _showAlert)
				{
					IsVisible = true;
				}
			}, RxApp.MainThreadScheduler);
		}
		catch(Exception ex)
		{
			DivinityApp.Log($"Error checking for update:\n{ex}");
			if (_showAlert) MainWindow.Self.ViewModel.ShowAlert($"Error occurred when checking for updates: {ex.Message}", AlertType.Danger, 60);

			if (ex is System.Net.WebException)
			{
				MainWindow.Self.DisplayError("Update Check Failed", "There was a problem reaching the update server. Please check your internet connection and try again later.", false);
			}
		}
	}

	public void ScheduleUpdateCheck(bool showAlerts = false)
	{
		_showAlert = showAlerts;
		AutoUpdater.ReportErrors = _showAlert;
		AutoUpdater.Start(DivinityApp.URL_UPDATE);
	}

	public AppUpdateWindowViewModel()
	{
		OnUpdateCheckCommand = ReactiveCommand.CreateFromTask<UpdateInfoEventArgs>(OnUpdateCheckAsync, null, RxApp.TaskpoolScheduler);

		//Observable.FromEventPattern<AutoUpdater.CheckForUpdateEventHandler, UpdateInfoEventArgs>(
		//  handler => AutoUpdater.CheckForUpdateEvent += handler,
		//  handler => AutoUpdater.CheckForUpdateEvent -= handler).ObserveOn(RxApp.TaskpoolScheduler).InvokeCommand(OnUpdateCheckCommand);

		var canConfirm = this.WhenAnyValue(x => x.CanConfirm);
		ConfirmCommand = ReactiveCommand.Create(() =>
		{
			TryRunUpdate();
		}, canConfirm, RxApp.MainThreadScheduler);

		var canSkip = this.WhenAnyValue(x => x.CanSkip);
		SkipCommand = ReactiveCommand.Create(() => IsVisible = false, canSkip);
		CanSkip = true;
	}
}