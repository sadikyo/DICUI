using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;
using BurnOutSharp;
using DICUI.Data;
using DICUI.Utilities;
using DICUI.Web;
using Newtonsoft.Json.Linq;

namespace DICUI.Windows
{
    public partial class MainWindow : Window
    {
        // Private UI-related variables
        private List<Drive> _drives;
        private MediaType? _currentMediaType;
        private List<KnownSystemComboBoxItem> _systems;
        private List<MediaType?> _mediaTypes;
        private bool _alreadyShown;

        private DumpEnvironment _env;

        // Option related
        private UIOptions _uiOptions;
        private OptionsWindow _optionsWindow;

        // User input related
        private DiscInformationWindow _discInformationWindow;

        private LogWindow _logWindow;

        public MainWindow()
        {
            InitializeComponent();

            // Initializes and load Options object
            _uiOptions = new UIOptions();
            ViewModels.OptionsViewModel = new OptionsViewModel(_uiOptions);

            _logWindow = new LogWindow(this);
            ViewModels.LoggerViewModel.SetWindow(_logWindow);

            // Disable buttons until we load fully
            StartStopButton.IsEnabled = false;
            DiskScanButton.IsEnabled = false;
            CopyProtectScanButton.IsEnabled = false;

            if (_uiOptions.OpenLogWindowAtStartup)
            {
                this.WindowStartupLocation = WindowStartupLocation.Manual;
                double combinedHeight = this.Height + _logWindow.Height + Constants.LogWindowMarginFromMainWindow;
                Rectangle bounds = GetScaledCoordinates(WinForms.Screen.PrimaryScreen.WorkingArea);

                this.Left = bounds.Left + (bounds.Width - this.Width) / 2;
                this.Top = bounds.Top + (bounds.Height - combinedHeight) / 2;
            }
        }

        #region Events

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            if (_alreadyShown)
                return;

            _alreadyShown = true;

            if (_uiOptions.OpenLogWindowAtStartup)
            {
                //TODO: this should be bound directly to WindowVisible property in two way fashion
                // we need to study how to properly do it in XAML
                ShowLogMenuItem.IsChecked = true;
                ViewModels.LoggerViewModel.WindowVisible = true;
            }

            // Populate the list of systems
            StatusLabel.Content = "Creating system list, please wait!";
            PopulateSystems();

            // Populate the list of drives
            StatusLabel.Content = "Creating drive list, please wait!";
            PopulateDrives();
        }

        private void StartStopButtonClick(object sender, RoutedEventArgs e)
        {
            // Dump or stop the dump
            if ((string)StartStopButton.Content == Constants.StartDumping)
            {
                StartDumping();
            }
            else if ((string)StartStopButton.Content == Constants.StopDumping)
            {
                ViewModels.LoggerViewModel.VerboseLogLn("Canceling dumping process...");
                _env.CancelDumping();
                CopyProtectScanButton.IsEnabled = true;

                if (EjectWhenDoneCheckBox.IsChecked == true)
                {
                    ViewModels.LoggerViewModel.VerboseLogLn($"Ejecting disc in drive {_env.Drive.Letter}");
                    _env.EjectDisc();
                }

                if (_uiOptions.ResetDriveAfterDump)
                {
                    ViewModels.LoggerViewModel.VerboseLogLn($"Resetting drive {_env.Drive.Letter}");
                    _env.ResetDrive();
                }
            }
        }

        private void OutputDirectoryBrowseButtonClick(object sender, RoutedEventArgs e)
        {
            BrowseFolder();
            EnsureDiscInformation();
        }

        private void DiskScanButtonClick(object sender, RoutedEventArgs e)
        {
            PopulateDrives();
        }

        private void CopyProtectScanButtonClick(object sender, RoutedEventArgs e)
        {
            ScanAndShowProtection();
        }

        private void SystemTypeComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // If we're on a separator, go to the next item and return
            if ((SystemTypeComboBox.SelectedItem as KnownSystemComboBoxItem).IsHeader())
            {
                SystemTypeComboBox.SelectedIndex++;
                return;
            }

            ViewModels.LoggerViewModel.VerboseLogLn("Changed system to: {0}", (SystemTypeComboBox.SelectedItem as KnownSystemComboBoxItem).Name);
            PopulateMediaType();
            GetOutputNames(false);
            EnsureDiscInformation();
        }

        private void MediaTypeComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Only change the media type if the selection and not the list has changed
            if (e.RemovedItems.Count == 1 && e.AddedItems.Count == 1)
            {
                _currentMediaType = MediaTypeComboBox.SelectedItem as MediaType?;
                SetSupportedDriveSpeed();
            }

            GetOutputNames(false);
            EnsureDiscInformation();
        }

        private void DriveLetterComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CacheCurrentDiscType();
            SetCurrentDiscType();
            GetOutputNames(true);
            SetSupportedDriveSpeed();
        }

        private void DriveSpeedComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EnsureDiscInformation();
        }

        private void OutputFilenameTextBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            EnsureDiscInformation();
        }

        private void OutputDirectoryTextBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            EnsureDiscInformation();
        }

        private void ProgressUpdated(object sender, Result value)
        {
            StatusLabel.Content = value.Message;
            ViewModels.LoggerViewModel.VerboseLogLn(value.Message);
        }

        private void ProgressUpdated(object sender, FileProtection value)
        {
            string message = $"{value.Percentage * 100:N2}%: {value.Filename} - {value.Protection}";
            StatusLabel.Content = message;
            ViewModels.LoggerViewModel.VerboseLogLn(message);
        }

        private void MainWindowLocationChanged(object sender, EventArgs e)
        {
            if (_logWindow.IsVisible)
                _logWindow.AdjustPositionToMainWindow();
        }

        private void MainWindowActivated(object sender, EventArgs e)
        {
            if (_logWindow.IsVisible && !this.Topmost)
            {
                _logWindow.Topmost = true;
                _logWindow.Topmost = false;
            }
        }

        private void MainWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_logWindow.IsVisible)
                _logWindow.Close();
        }

        private void EnableParametersCheckBoxClick(object sender, RoutedEventArgs e)
        {
            if (EnableParametersCheckBox.IsChecked == true)
                ParametersTextBox.IsEnabled = true;
            else
            {
                ParametersTextBox.IsEnabled = false;
                ProcessCustomParameters();
            }
        }

        // Toolbar Events

        private void AppExitClick(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void AboutClick(object sender, RoutedEventArgs e)
        {
            MessageBox.Show($"darksabre76 - Project Lead / Backend Design"
                + $"{Environment.NewLine}ReignStumble - Former Project Lead / UI Design"
                + $"{Environment.NewLine}Jakz - Primary Feature Contributor"
                + $"{Environment.NewLine}NHellFire - Feature Contributor", "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OptionsClick(object sender, RoutedEventArgs e)
        {
            // lazy initialization
            if (_optionsWindow == null)
            {
                _optionsWindow = new OptionsWindow(this, _uiOptions);
                _optionsWindow.Closed += delegate
                {
                    _optionsWindow = null;
                };
            }

            _optionsWindow.Owner = this;
            _optionsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _optionsWindow.Refresh();
            _optionsWindow.Show();
        }

        private void CheckForUpdatesClick(object sender, RoutedEventArgs e)
        {
            // Get the current internal version
            var assemblyVersion = Assembly.GetEntryAssembly().GetName().Version;
            string version = $"{assemblyVersion.Major}.{assemblyVersion.Minor}" + (assemblyVersion.MajorRevision != 0 ? $".{assemblyVersion.MajorRevision}" : string.Empty);

            // Get the latest tag from GitHub
            using (var client = new RedumpWebClient())
            {
                client.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 6.1; WOW64; rv:64.0) Gecko/20100101 Firefox/64.0";

                // TODO: Figure out a better way than having this hardcoded...
                string url = "https://api.github.com/repos/SabreTools/DICUI/releases/latest";
                string latestReleaseJsonString = client.DownloadString(url);
                var latestReleaseJson = JObject.Parse(latestReleaseJsonString);
                string latestTag = latestReleaseJson["tag_name"].ToString();
                string releaseUrl = latestReleaseJson["html_url"].ToString();

                bool different = version != latestTag;

                string message = $"Local version: {version}"
                    + $"{Environment.NewLine}Remote version: {latestTag}"
                    + (different
                        ? $"{Environment.NewLine}The update URL has been added copied to your clipboard"
                        : $"{Environment.NewLine}You have the newest version!");

                // If we have a new version, put it in the clipboard
                if (different)
                    Clipboard.SetText(releaseUrl);

                MessageBox.Show(message, "Version Update Check", MessageBoxButton.OK, different ? MessageBoxImage.Exclamation : MessageBoxImage.Information);
            }
        }

        public void OnOptionsUpdated()
        {
            PopulateDrives();
            GetOutputNames(false);
            SetSupportedDriveSpeed();
            EnsureDiscInformation();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Populate media type according to system type
        /// </summary>
        private void PopulateMediaType()
        {
            KnownSystem? currentSystem = SystemTypeComboBox.SelectedItem as KnownSystemComboBoxItem;

            if (currentSystem != null)
            {
                _mediaTypes = Validators.GetValidMediaTypes(currentSystem);
                MediaTypeComboBox.ItemsSource = _mediaTypes;

                MediaTypeComboBox.IsEnabled = _mediaTypes.Count > 1;
                MediaTypeComboBox.SelectedIndex = (_mediaTypes.IndexOf(_currentMediaType) >= 0 ? _mediaTypes.IndexOf(_currentMediaType) : 0);
            }
            else
            {
                MediaTypeComboBox.IsEnabled = false;
                MediaTypeComboBox.ItemsSource = null;
                MediaTypeComboBox.SelectedIndex = -1;
            }
        }

        /// <summary>
        /// Get a complete list of supported systems and fill the combo box
        /// </summary>
        private void PopulateSystems()
        {
            var knownSystems = Validators.CreateListOfSystems();

            ViewModels.LoggerViewModel.VerboseLogLn("Populating systems, {0} systems found.", knownSystems.Count);

            Dictionary<KnownSystemCategory, List<KnownSystem?>> mapping = knownSystems
                .GroupBy(s => s.Category())
                .ToDictionary(
                    k => k.Key,
                    v => v
                        .OrderBy(s => s.LongName())
                        .ToList()
                );

            _systems = new List<KnownSystemComboBoxItem>();
            _systems.Add(new KnownSystemComboBoxItem(KnownSystem.NONE));

            foreach (var group in mapping)
            {
                _systems.Add(new KnownSystemComboBoxItem(group.Key));
                group.Value.ForEach(system => _systems.Add(new KnownSystemComboBoxItem(system)));
            }

            SystemTypeComboBox.ItemsSource = _systems;
            SystemTypeComboBox.SelectedIndex = 0;

            StartStopButton.IsEnabled = false;
        }

        /// <summary>
        /// Get a complete list of active disc drives and fill the combo box
        /// </summary>
        /// <remarks>TODO: Find a way for this to periodically run, or have it hook to a "drive change" event</remarks>
        private void PopulateDrives()
        {
            ViewModels.LoggerViewModel.VerboseLogLn("Scanning for drives..");

            // Always enable the disk scan
            DiskScanButton.IsEnabled = true;

            // Populate the list of drives and add it to the combo box
            _drives = Validators.CreateListOfDrives(_uiOptions.IgnoreFixedDrives);
            DriveLetterComboBox.ItemsSource = _drives;

            if (DriveLetterComboBox.Items.Count > 0)
            {
                // Check for active optical drives first
                int index = _drives.FindIndex(d => d.MarkedActive && d.InternalDriveType == InternalDriveType.Optical);

                // Then we check for floppy drives
                if (index == -1)
                    index = _drives.FindIndex(d => d.MarkedActive && d.InternalDriveType == InternalDriveType.Floppy);

                // Then we try all other drive types
                if (index == -1)
                    index = _drives.FindIndex(d => d.MarkedActive);

                // Set the selected index
                DriveLetterComboBox.SelectedIndex = (index != -1 ? index : 0);
                StatusLabel.Content = "Valid drive found! Choose your Media Type";
                                CopyProtectScanButton.IsEnabled = true;

                // Get the current media type
                if (!_uiOptions.SkipSystemDetection && index != -1)
                {
                    ViewModels.LoggerViewModel.VerboseLog("Trying to detect system for drive {0}.. ", _drives[index].Letter);
                    var currentSystem = Validators.GetKnownSystem(_drives[index]);
                    ViewModels.LoggerViewModel.VerboseLogLn(currentSystem == null || currentSystem == KnownSystem.NONE ? "unable to detect." : ("detected " + currentSystem.LongName() + "."));

                    if (currentSystem != null && currentSystem != KnownSystem.NONE)
                    {
                        int sysIndex = _systems.FindIndex(s => s == currentSystem);
                        SystemTypeComboBox.SelectedIndex = sysIndex;
                    }
                }

                // Only enable the start/stop if we don't have the default selected
                StartStopButton.IsEnabled = (SystemTypeComboBox.SelectedItem as KnownSystemComboBoxItem) != KnownSystem.NONE;

                ViewModels.LoggerViewModel.VerboseLogLn("Found {0} drives: {1}", _drives.Count, string.Join(", ", _drives.Select(d => d.Letter)));
            }
            else
            {
                DriveLetterComboBox.SelectedIndex = -1;
                StatusLabel.Content = "No valid drive found!";
                StartStopButton.IsEnabled = false;
                CopyProtectScanButton.IsEnabled = false;

                ViewModels.LoggerViewModel.VerboseLogLn("Found no drives");
            }
        }

        /// <summary>
        /// Browse for an output folder
        /// </summary>
        private void BrowseFolder()
        {
            WinForms.FolderBrowserDialog folderDialog = new WinForms.FolderBrowserDialog { ShowNewFolderButton = false, SelectedPath = System.AppDomain.CurrentDomain.BaseDirectory };
            WinForms.DialogResult result = folderDialog.ShowDialog();

            if (result == WinForms.DialogResult.OK)
            {
                OutputDirectoryTextBox.Text = folderDialog.SelectedPath;
            }
        }

        /// <summary>
        /// Create a DumpEnvironment with all current settings
        /// </summary>
        /// <returns>Filled DumpEnvironment instance</returns>
        private DumpEnvironment DetermineEnvironment()
        {
            // Populate the new environment
            var env = new DumpEnvironment(_uiOptions.Options,
                OutputDirectoryTextBox.Text,
                OutputFilenameTextBox.Text,
                DriveLetterComboBox.SelectedItem as Drive,
                SystemTypeComboBox.SelectedItem as KnownSystemComboBoxItem,
                MediaTypeComboBox.SelectedItem as MediaType?,
                ParametersTextBox.Text);

            // Disable automatic reprocessing of the textboxes until we're done
            OutputDirectoryTextBox.TextChanged -= OutputDirectoryTextBoxTextChanged;
            OutputFilenameTextBox.TextChanged -= OutputFilenameTextBoxTextChanged;

            OutputDirectoryTextBox.Text = env.OutputDirectory;
            OutputFilenameTextBox.Text = env.OutputFilename;

            OutputDirectoryTextBox.TextChanged += OutputDirectoryTextBoxTextChanged;
            OutputFilenameTextBox.TextChanged += OutputFilenameTextBoxTextChanged;

            return env;
        }

        /// <summary>
        /// Begin the dumping process using the given inputs
        /// </summary>
        private async void StartDumping()
        {
            // One last check to determine environment, just in case
            _env = DetermineEnvironment();

            // If still in custom parameter mode, check that users meant to continue or not
            if (EnableParametersCheckBox.IsChecked == true)
            {
                MessageBoxResult result = MessageBox.Show("It looks like you have custom parameters that have not been saved. Would you like to apply those changes before starting to dump?", "Custom Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    EnableParametersCheckBox.IsChecked = false;
                    ParametersTextBox.IsEnabled = false;
                    ProcessCustomParameters();
                }
                else if (result == MessageBoxResult.Cancel)
                    return;
                // If "No", then we continue with the current known environment
            }

            // Fix the output paths
            _env.FixOutputPaths();

            try
            {
                // Validate that the user explicitly wants an inactive drive to be considered for dumping
                if (!_env.Drive.MarkedActive)
                {
                    MessageBoxResult mbresult = MessageBox.Show("The currently selected drive does not appear to contain a disc! Are you sure you want to continue?", "Missing Disc", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);
                    if (mbresult == MessageBoxResult.No || mbresult == MessageBoxResult.Cancel || mbresult == MessageBoxResult.None)
                    {
                        ViewModels.LoggerViewModel.VerboseLogLn("Dumping aborted!");
                        return;
                    }
                }

                // If a complete dump already exists
                if (_env.FoundAllFiles())
                {
                    MessageBoxResult mbresult = MessageBox.Show("A complete dump already exists! Are you sure you want to overwrite?", "Overwrite?", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);
                    if (mbresult == MessageBoxResult.No || mbresult == MessageBoxResult.Cancel || mbresult == MessageBoxResult.None)
                    {
                        ViewModels.LoggerViewModel.VerboseLogLn("Dumping aborted!");
                        return;
                    }
                }

                StartStopButton.Content = Constants.StopDumping;
                CopyProtectScanButton.IsEnabled = false;
                StatusLabel.Content = "Beginning dumping process";
                ViewModels.LoggerViewModel.VerboseLogLn("Starting dumping process..");

                // Get progress indicators
                var resultProgress = new Progress<Result>();
                resultProgress.ProgressChanged += ProgressUpdated;
                var protectionProgress = new Progress<FileProtection>();
                protectionProgress.ProgressChanged += ProgressUpdated;

                // Run the program with the parameters
                Result result = await _env.Run(resultProgress);

                // If we didn't execute a dumping command we cannot get submission output
                if (!_env.Parameters.IsDumpingCommand())
                {
                    ViewModels.LoggerViewModel.VerboseLogLn("No dumping command was run, submission information will not be gathered.");
                    StatusLabel.Content = "Execution complete!";
                    StartStopButton.Content = Constants.StartDumping;
                    CopyProtectScanButton.IsEnabled = true;
                    return;
                }

                if (result)
                {
                    // Verify dump output and save it
                    result = await _env.VerifyAndSaveDumpOutput(resultProgress,
                        protectionProgress,
                        EjectWhenDoneCheckBox.IsChecked,
                        _uiOptions.ResetDriveAfterDump,
                        (si) =>
                        {
                            // lazy initialization
                            if (_discInformationWindow == null)
                            {
                                _discInformationWindow = new DiscInformationWindow(this, si);
                                _discInformationWindow.Closed += delegate
                                {
                                    _discInformationWindow = null;
                                };
                            }

                            _discInformationWindow.Owner = this;
                            _discInformationWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                            _discInformationWindow.Refresh();
                            return _discInformationWindow.ShowDialog();
                        }
                    );
                }
            }
            catch
            {
                // No-op, we don't care what it was
            }
            finally
            {
                StartStopButton.Content = Constants.StartDumping;
                CopyProtectScanButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Ensure information is consistent with the currently selected disc type
        /// </summary>
        private void EnsureDiscInformation()
        {
            // Get the current environment information
            _env = DetermineEnvironment();

            // Take care of null cases
            if (_env.System == null)
                _env.System = KnownSystem.NONE;
            if (_env.Type == null)
                _env.Type = MediaType.NONE;

            // Get the status to write out
            Result result = Validators.GetSupportStatus(_env.System, _env.Type);
            StatusLabel.Content = result.Message;

            // Set the index for the current disc type
            SetCurrentDiscType();

            StartStopButton.IsEnabled = result && (_drives != null && _drives.Count > 0 ? true : false);

            // If we're in a type that doesn't support drive speeds
            DriveSpeedComboBox.IsEnabled = _env.Type.DoesSupportDriveSpeed();

            // If input params are not enabled, generate the full parameters from the environment
            if (!ParametersTextBox.IsEnabled)
            {
                string generated = _env.GetFullParameters((int?)DriveSpeedComboBox.SelectedItem);
                if (generated != null)
                    ParametersTextBox.Text = generated;
            }
        }

        /// <summary>
        /// Get the default output directory name from the currently selected drive
        /// </summary>
        /// <param name="driveChanged">Force an updated name if the drive letter changes</param>
        private void GetOutputNames(bool driveChanged)
        {
            Drive drive = DriveLetterComboBox.SelectedItem as Drive;
            KnownSystem? systemType = SystemTypeComboBox.SelectedItem as KnownSystemComboBoxItem;
            MediaType? mediaType = MediaTypeComboBox.SelectedItem as MediaType?;

            // Set the output directory, if we changed drives or it's not already
            if (driveChanged || string.IsNullOrEmpty(OutputDirectoryTextBox.Text))
                OutputDirectoryTextBox.Text = Path.Combine(_uiOptions.DefaultOutputPath, drive?.VolumeLabel ?? string.Empty);

            // Get the extension for the file for the next two statements
            string extension = _env.GetExtension(mediaType);

            // Set the output filename, if we changed drives or it's not already
            if (driveChanged || string.IsNullOrEmpty(OutputFilenameTextBox.Text))
                OutputFilenameTextBox.Text = (drive?.VolumeLabel ?? systemType.LongName()) + (extension ?? ".bin");

            // If the extension for the file changed, update that automatically
            else if (Path.GetExtension(OutputFilenameTextBox.Text) != extension)
                OutputFilenameTextBox.Text = Path.GetFileNameWithoutExtension(OutputFilenameTextBox.Text) + (extension ?? ".bin");
        }

        /// <summary>
        /// Scan and show copy protection for the current disc
        /// </summary>
        private async void ScanAndShowProtection()
        {
            if (_env == null)
                _env = DetermineEnvironment();

            if (_env.Drive.Letter != default(char))
            {
                ViewModels.LoggerViewModel.VerboseLogLn("Scanning for copy protection in {0}", _env.Drive.Letter);

                var tempContent = StatusLabel.Content;
                StatusLabel.Content = "Scanning for copy protection... this might take a while!";
                StartStopButton.IsEnabled = false;
                DiskScanButton.IsEnabled = false;
                CopyProtectScanButton.IsEnabled = false;

                var progress = new Progress<FileProtection>();
                progress.ProgressChanged += ProgressUpdated;
                string protections = await Validators.RunProtectionScanOnPath(_env.Drive.Letter + ":\\", progress);

                // If SmartE is detected on the current disc, remove `/sf` from the flags for DIC only
                if (_env.InternalProgram == InternalProgram.DiscImageCreator && protections.Contains("SmartE"))
                {
                    ((DiscImageCreator.Parameters)_env.Parameters)[DiscImageCreator.Flag.ScanFileProtect] = false;
                    ViewModels.LoggerViewModel.VerboseLogLn($"SmartE detected, removing {DiscImageCreator.FlagStrings.ScanFileProtect} from parameters");
                }

                if (!ViewModels.LoggerViewModel.WindowVisible)
                    MessageBox.Show(protections, "Detected Protection", MessageBoxButton.OK, MessageBoxImage.Information);
                ViewModels.LoggerViewModel.VerboseLog("Detected the following protections in {0}:\r\n\r\n{1}", _env.Drive.Letter, protections);

                StatusLabel.Content = tempContent;
                StartStopButton.IsEnabled = true;
                DiskScanButton.IsEnabled = true;
                CopyProtectScanButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Set the drive speed based on reported maximum and user-defined option
        /// </summary>
        private void SetSupportedDriveSpeed()
        {
            // Set the drive speed list that's appropriate
            var values = Constants.GetSpeedsForMediaType(_currentMediaType);
            DriveSpeedComboBox.ItemsSource = values;
            ViewModels.LoggerViewModel.VerboseLogLn("Supported media speeds: {0}", string.Join(",", values));

            // Set the selected speed
            int speed = _uiOptions.GetPreferredDumpSpeedForMediaType(_currentMediaType);
            ViewModels.LoggerViewModel.VerboseLogLn("Setting drive speed to: {0}", speed);
            DriveSpeedComboBox.SelectedValue = speed;
        }

        /// <summary>
        /// Cache the current disc type to internal variable
        /// </summary>
        private void CacheCurrentDiscType()
        {
            // Get the drive letter from the selected item
            var drive = DriveLetterComboBox.SelectedItem as Drive;
            if (drive == null)
                return;

            // Get the current media type
            if (!_uiOptions.SkipMediaTypeDetection)
            {
                ViewModels.LoggerViewModel.VerboseLog("Trying to detect media type for drive {0}.. ", drive.Letter);
                _currentMediaType = Validators.GetMediaType(drive);
                ViewModels.LoggerViewModel.VerboseLogLn(_currentMediaType == null ? "unable to detect." : ("detected " + _currentMediaType.LongName() + "."));
            }
        }

        /// <summary>
        /// Set the current disc type in the combo box
        /// </summary>
        private void SetCurrentDiscType()
        {
            // If we have an invalid current type, we don't care and return
            if (_currentMediaType == null || _currentMediaType == MediaType.NONE)
                return;

            // Now set the selected item, if possible
            int index = _mediaTypes.FindIndex(kvp => kvp.Value == _currentMediaType);
            if (index != -1)
                MediaTypeComboBox.SelectedIndex = index;
            else
                StatusLabel.Content = $"Disc of type '{Converters.LongName(_currentMediaType)}' found, but the current system does not support it!";
        }

        /// <summary>
        /// Process the current custom parameters back into UI values
        /// </summary>
        private void ProcessCustomParameters()
        {
            _env.SetParameters(ParametersTextBox.Text);
            if (_env.Parameters == null)
                return;

            int driveIndex = _drives.Select(d => d.Letter).ToList().IndexOf(_env.Parameters.InputPath()[0]);
            if (driveIndex > -1)
                DriveLetterComboBox.SelectedIndex = driveIndex;

            int driveSpeed = _env.Parameters.GetSpeed() ?? -1;
            if (driveSpeed > 0)
                DriveSpeedComboBox.SelectedValue = driveSpeed;
            else
                _env.Parameters.SetSpeed((int?)DriveSpeedComboBox.SelectedValue);

            string trimmedPath = _env.Parameters.OutputPath()?.Trim('"') ?? string.Empty;
            string outputDirectory = Path.GetDirectoryName(trimmedPath);
            string outputFilename = Path.GetFileName(trimmedPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                OutputDirectoryTextBox.Text = outputDirectory;
            else
                outputDirectory = OutputDirectoryTextBox.Text;
            if (!string.IsNullOrWhiteSpace(outputFilename))
                OutputFilenameTextBox.Text = outputFilename;
            else
                outputFilename = OutputFilenameTextBox.Text;

            MediaType? mediaType = _env.Parameters.GetMediaType();
            int mediaTypeIndex = _mediaTypes.IndexOf(mediaType);
            if (mediaTypeIndex > -1)
                MediaTypeComboBox.SelectedIndex = mediaTypeIndex;
        }

        #endregion

        #region UI Helpers

        /// <summary>
        /// Get pixel coordinates based on DPI scaling
        /// </summary>
        /// <param name="bounds">Rectangle representing the bounds to transform</param>
        /// <returns>Rectangle representing the scaled bounds</returns>
        private Rectangle GetScaledCoordinates(Rectangle bounds)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                return new Rectangle(
                TransformCoordinate(bounds.Left, g.DpiX),
                TransformCoordinate(bounds.Top, g.DpiY),
                TransformCoordinate(bounds.Width, g.DpiX),
                TransformCoordinate(bounds.Height, g.DpiY));
            }
        }

        /// <summary>
        /// Transform an individual coordinate using DPI scaling
        /// </summary>
        /// <param name="coord">Current integer coordinate</param>
        /// <param name="dpi">DPI scaling factor</param>
        /// <returns>Scaled integer coordinate</returns>
        private int TransformCoordinate(int coord, float dpi)
        {
            return (int)(coord / ((double)dpi / 96));
        }

        #endregion
    }
}
