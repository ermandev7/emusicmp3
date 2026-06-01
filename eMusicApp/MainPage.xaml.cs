namespace eMusicApp;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
		DownloadManager.Initialize();

		NativeAudioController.OnProgressUpdated = (posMs, durMs) =>
		{
			MainThread.BeginInvokeOnMainThread(async () =>
			{
				try { await AppWebView.EvaluateJavaScriptAsync($"if(window.updateNativeProgress) window.updateNativeProgress({posMs}, {durMs});"); } catch { }
			});
		};

		NativeAudioController.OnTrackEnded = () =>
		{
			MainThread.BeginInvokeOnMainThread(async () =>
			{
				try { await AppWebView.EvaluateJavaScriptAsync($"if(window.onNativeTrackEnded) window.onNativeTrackEnded();"); } catch { }
			});
		};

		NativeAudioController.OnSkipToNext = () =>
		{
			MainThread.BeginInvokeOnMainThread(async () =>
			{
				try { await AppWebView.EvaluateJavaScriptAsync($"if(window.playNativeNext) window.playNativeNext();"); } catch { }
			});
		};

		NativeAudioController.OnSkipToPrevious = () =>
		{
			MainThread.BeginInvokeOnMainThread(async () =>
			{
				try { await AppWebView.EvaluateJavaScriptAsync($"if(window.playNativePrevious) window.playNativePrevious();"); } catch { }
			});
		};

		NativeAudioController.OnSearchRequested = (query) =>
		{
			MainThread.BeginInvokeOnMainThread(async () =>
			{
				var escapedQuery = query.Replace("'", "\\'").Replace("\"", "\\\"");
				try { await AppWebView.EvaluateJavaScriptAsync($"if(window.playFromNativeSearch) window.playFromNativeSearch('{escapedQuery}');"); } catch { }
			});
		};

		NativeAudioController.OnPlaybackStateChanged = (isPlaying) =>
		{
			MainThread.BeginInvokeOnMainThread(async () =>
			{
				try { await AppWebView.EvaluateJavaScriptAsync($"if(window.onNativePlaybackStateChanged) window.onNativePlaybackStateChanged({isPlaying.ToString().ToLower()});"); } catch { }
			});
		};
	}

	private void OnWebViewNavigating(object sender, WebNavigatingEventArgs e)
	{
		if (e.Url.StartsWith("emusic://"))
		{
			// Cancel navigation so the WebView doesn't actually change page
			e.Cancel = true;

			try
			{
				var uri = new Uri(e.Url);
				var command = uri.Host; // "play", "pause", "resume"
				var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

				if (command == "play")
				{
					string audioUrl = query["url"];
					string title = query["title"] ?? "";
					string artist = query["artist"] ?? "";
					string thumb = query["thumb"] ?? "";
					string id = query["id"] ?? "";

					if (!string.IsNullOrEmpty(audioUrl))
					{
						// Check if downloaded
						string localPath = null;
						if (!string.IsNullOrEmpty(id)) {
							localPath = DownloadManager.GetLocalPath(id);
						}

						if (!string.IsNullOrEmpty(localPath)) {
							// Play local file
							NativeAudioController.RequestPlay(localPath, title, artist, thumb);
						} else {
							// Play streaming URL
							NativeAudioController.RequestPlay(audioUrl, title, artist, thumb);
						}
					}
				}
				else if (command == "pause")
				{
					NativeAudioController.RequestPause();
				}
				else if (command == "resume")
				{
					NativeAudioController.RequestResume();
				}
				else if (command == "seek")
				{
					if (int.TryParse(query["position"], out int positionMs))
					{
						NativeAudioController.RequestSeek(positionMs);
					}
				}
				else if (command == "color")
				{
					string hex = query["hex"];
					if (!string.IsNullOrEmpty(hex))
					{
						SetStatusBarColor("#" + hex);
					}
				}
				else if (command == "download")
				{
					string id = query["id"];
					string url = query["url"];
					string title = query["title"] ?? "";
					string artist = query["artist"] ?? "";
					string thumb = query["thumb"] ?? "";

					if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(url))
					{
						System.Threading.Tasks.Task.Run(async () => {
							await DownloadManager.DownloadTrackAsync(id, url, title, artist, thumb);
							// Notify webview that download is complete
							MainThread.BeginInvokeOnMainThread(async () => {
								string escapedId = id.Replace("'", "\\'");
								try { await AppWebView.EvaluateJavaScriptAsync($"if(window.onNativeDownloadComplete) window.onNativeDownloadComplete('{escapedId}');"); } catch { }
							});
						});
					}
				}
				else if (command == "preparenext")
				{
					string audioUrl = query["url"];
					string title = query["title"] ?? "";
					string artist = query["artist"] ?? "";
					string thumb = query["thumb"] ?? "";
					string id = query["id"] ?? "";

					if (!string.IsNullOrEmpty(audioUrl))
					{
						string localPath = null;
						if (!string.IsNullOrEmpty(id)) {
							localPath = DownloadManager.GetLocalPath(id);
						}

						if (!string.IsNullOrEmpty(localPath)) {
							NativeAudioController.RequestPrepareNext(localPath, title, artist, thumb);
						} else {
							NativeAudioController.RequestPrepareNext(audioUrl, title, artist, thumb);
						}
					}
				}
				else if (command == "startcrossfade")
				{
					NativeAudioController.RequestStartCrossfade();
				}
				else if (command == "deletedownload")
				{
					string id = query["id"];
					if (!string.IsNullOrEmpty(id))
					{
						DownloadManager.DeleteTrack(id);
						string escapedId = id.Replace("'", "\\'");
						MainThread.BeginInvokeOnMainThread(async () => {
							try { await AppWebView.EvaluateJavaScriptAsync($"if(window.onNativeDownloadDeleted) window.onNativeDownloadDeleted('{escapedId}');"); } catch { }
						});
					}
				}
				else if (command == "getdownloads")
				{
					string json = DownloadManager.GetDownloadedTracksJson();
					// Send the json back to webview
					string escapedJson = json.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "");
					MainThread.BeginInvokeOnMainThread(async () => {
						try { await AppWebView.EvaluateJavaScriptAsync($"if(window.onNativeDownloadsReceived) window.onNativeDownloadsReceived('{escapedJson}');"); } catch { }
					});
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error parsing emusic URL: {ex.Message}");
			}
		}
	}

	private async void OnWebViewNavigated(object sender, WebNavigatedEventArgs e)
	{
		try
		{
			await AppWebView.EvaluateJavaScriptAsync("window.isNativeApp = true;");
			await AppWebView.EvaluateJavaScriptAsync("if (window.disableWebViewAudio) window.disableWebViewAudio();");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error injecting JS: {ex.Message}");
		}
	}

	private void SetStatusBarColor(string hexColor)
		{
#if ANDROID
			try
			{
				var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
				if (activity != null && activity.Window != null)
				{
					activity.Window.SetStatusBarColor(Android.Graphics.Color.ParseColor(hexColor));
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error setting status bar color: {ex.Message}");
			}
#endif
		}
	}
