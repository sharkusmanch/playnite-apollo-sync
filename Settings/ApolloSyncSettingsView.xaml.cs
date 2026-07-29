using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace ApolloSync
{
    public partial class ApolloSyncSettingsView : UserControl
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // Held directly rather than looked up with FindChild: the Manage Games tab is not the
        // selected tab when the window opens, so its content is not in the visual tree yet and
        // a VisualTreeHelper search returns null.
        private StackPanel _managedGamesPanel;

        public ApolloSyncSettingsView()
        {
            BuildUI();

            // Auto-load managed games when the settings window opens. BuildUI runs before
            // Playnite assigns the DataContext, so the list cannot be populated there.
            Loaded += (sender, e) =>
            {
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    if (DataContext != null && _managedGamesPanel != null)
                    {
                        RefreshManagedGamesList(_managedGamesPanel);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        private void BuildUI()
        {
            var tabControl = new TabControl { Margin = new Thickness(8) };

            // General Tab
            var generalTab = new TabItem
            {
                Header = ResourceProvider.GetString("LOC_ApolloSync_Settings_Tab_General")
            };
            generalTab.Content = BuildGeneralTab();
            tabControl.Items.Add(generalTab);

            // Filters Tab
            var filtersTab = new TabItem
            {
                Header = ResourceProvider.GetString("LOC_ApolloSync_Settings_Tab_Filters")
            };
            filtersTab.Content = BuildFiltersTab();
            tabControl.Items.Add(filtersTab);

            // Manage Games Tab
            var manageTab = new TabItem
            {
                Header = ResourceProvider.GetString("LOC_ApolloSync_Settings_Tab_ManageGames"),
                Name = "ManageGamesTab"
            };
            manageTab.Content = BuildManageGamesTab();
            tabControl.Items.Add(manageTab);

            Content = tabControl;
        }

        private StackPanel BuildGeneralTab()
        {
            var stack = new StackPanel { Margin = new Thickness(8) };

            // Sync when section
            var syncWhenHeader = new TextBlock
            {
                Text = "Sync when:",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(syncWhenHeader);

            // Settings updated
            var chkSyncOnUpdate = new CheckBox { Content = "Settings updated", Margin = new Thickness(20, 0, 0, 4) };
            chkSyncOnUpdate.SetBinding(CheckBox.IsCheckedProperty, new Binding("Settings.SyncOnSettingsUpdated") { Mode = BindingMode.TwoWay });
            stack.Children.Add(chkSyncOnUpdate);

            // Library updated
            var chkSyncOnLibUpdate = new CheckBox { Content = "Library updated", Margin = new Thickness(20, 0, 0, 4) };
            chkSyncOnLibUpdate.SetBinding(CheckBox.IsCheckedProperty, new Binding("Settings.SyncOnLibraryUpdate") { Mode = BindingMode.TwoWay });
            stack.Children.Add(chkSyncOnLibUpdate);

            // Playnite start
            var chkSyncOnStartup = new CheckBox { Content = "Playnite start", Margin = new Thickness(20, 0, 0, 4) };
            chkSyncOnStartup.SetBinding(CheckBox.IsCheckedProperty, new Binding("Settings.SyncOnStartup") { Mode = BindingMode.TwoWay });
            stack.Children.Add(chkSyncOnStartup);

            stack.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

            // Cover images
            var coverImagesHeader = new TextBlock
            {
                Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_CoverImages"),
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 8, 0, 8)
            };
            stack.Children.Add(coverImagesHeader);

            var chkManageCoverImages = new CheckBox
            {
                Content = ResourceProvider.GetString("LOC_ApolloSync_Settings_ManageCoverImages"),
                Margin = new Thickness(20, 0, 0, 4)
            };
            chkManageCoverImages.SetBinding(CheckBox.IsCheckedProperty,
                new Binding("Settings.ManageCoverImages") { Mode = BindingMode.TwoWay });
            stack.Children.Add(chkManageCoverImages);

            stack.Children.Add(new TextBlock
            {
                Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_ManageCoverImages_Help"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 0, 0, 8),
                FontStyle = FontStyles.Italic,
                Foreground = System.Windows.Media.Brushes.Gray
            });

            stack.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

            // Notifications header
            var notificationsHeader = new TextBlock
            {
                Text = "Notifications:",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 8, 0, 8)
            };
            stack.Children.Add(notificationsHeader);

            // Notification mode selector
            var notificationModePanel = new StackPanel { Margin = new Thickness(20, 0, 0, 8) };
            var notificationModeLabel = new TextBlock
            {
                Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_NotificationMode") + ":",
                Margin = new Thickness(0, 0, 0, 4)
            };
            notificationModePanel.Children.Add(notificationModeLabel);

            var notificationModeCombo = new ComboBox { Width = 250, HorizontalAlignment = HorizontalAlignment.Left };
            notificationModeCombo.Items.Add(new ComboBoxItem
            {
                Content = ResourceProvider.GetString("LOC_ApolloSync_Settings_NotificationMode_Always"),
                Tag = NotificationMode.Always
            });
            notificationModeCombo.Items.Add(new ComboBoxItem
            {
                Content = ResourceProvider.GetString("LOC_ApolloSync_Settings_NotificationMode_OnUpdateOnly"),
                Tag = NotificationMode.OnUpdateOnly
            });
            notificationModeCombo.Items.Add(new ComboBoxItem
            {
                Content = ResourceProvider.GetString("LOC_ApolloSync_Settings_NotificationMode_Never"),
                Tag = NotificationMode.Never
            });

            // Set initial selection based on settings
            notificationModeCombo.Loaded += (s, e) =>
            {
                if (DataContext is ApolloSyncSettingsViewModel vm)
                {
                    var mode = vm.Settings.NotificationMode;
                    foreach (ComboBoxItem item in notificationModeCombo.Items)
                    {
                        if (item.Tag is NotificationMode itemMode && itemMode == mode)
                        {
                            notificationModeCombo.SelectedItem = item;
                            break;
                        }
                    }
                }
            };

            // Handle selection changes
            notificationModeCombo.SelectionChanged += (s, e) =>
            {
                if (notificationModeCombo.SelectedItem is ComboBoxItem item &&
                    item.Tag is NotificationMode mode &&
                    DataContext is ApolloSyncSettingsViewModel vm)
                {
                    vm.Settings.NotificationMode = mode;
                }
            };

            notificationModePanel.Children.Add(notificationModeCombo);

            stack.Children.Add(notificationModePanel);

            // Help text for notifications
            var notificationHelpText = new TextBlock
            {
                Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_NotificationMode_Help"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 0, 0, 8),
                FontStyle = FontStyles.Italic,
                Foreground = System.Windows.Media.Brushes.Gray
            };
            stack.Children.Add(notificationHelpText);

            stack.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 16) });

            // Apps.json path section
            var pathHeader = new TextBlock
            {
                Text = "Apollo apps.json path:",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(pathHeader);

            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var txtPath = new TextBox { MinWidth = 300 };
            txtPath.SetBinding(TextBox.TextProperty, new Binding("Settings.AppsJsonPath") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            DockPanel.SetDock(txtPath, Dock.Left);
            dock.Children.Add(txtPath);
            var btnBrowse = new Button { Content = "Browse...", Width = 80, Margin = new Thickness(8, 0, 0, 0) };
            btnBrowse.Click += BrowseAppsJsonPath_Click;
            dock.Children.Add(btnBrowse);
            stack.Children.Add(dock);

            var helpText = new TextBlock
            {
                Text = "Path to Apollo's apps.json file. This file will be updated with your game library changes.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 0),
                FontStyle = FontStyles.Italic,
                Foreground = System.Windows.Media.Brushes.Gray
            };
            stack.Children.Add(helpText);

            return stack;
        }

        private FrameworkElement BuildFiltersTab()
        {
            // Outer scroll so both expanders (help + list + buttons) remain reachable when the
            // settings window is short — StackPanel alone was clipping the excluded section.
            var outerScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var stack = new StackPanel { Margin = new Thickness(8) };

            // Filter presets are the only filter mechanism. Platform, label, completion status
            // and category sections used to sit here, permanently Visibility.Collapsed and bound
            // to settings properties that never existed. Playnite's own filter presets already
            // cover those dimensions.

            // Filter Presets (collapsible)
            var filterPresetExpander = new Expander
            {
                Header = "Filter Presets",
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var filterPresetContentPanel = new StackPanel();

            filterPresetContentPanel.Children.Add(new TextBlock
            {
                Text = "Select one or more filter presets. Games that match ANY selected preset will be eligible for export (OR logic). Create filter presets in Playnite's main library view first.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = System.Windows.Media.Brushes.Gray,
                FontStyle = FontStyles.Italic
            });

            // Create filter preset selection table
            var filterPresetBorder = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 0)
            };

            var filterPresetScrollViewer = new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var filterPresetPanel = new UniformGrid { Name = "FilterPresetsPanel", Columns = 3 };
            filterPresetScrollViewer.Content = filterPresetPanel;
            filterPresetBorder.Child = filterPresetScrollViewer;
            filterPresetContentPanel.Children.Add(filterPresetBorder);

            // Add "Select All" and "Clear All" buttons for filter presets
            var filterPresetButtonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var selectAllFilterPresetBtn = new Button { Content = "Select All", Width = 80, Margin = new Thickness(0, 0, 4, 0) };
            var clearAllFilterPresetBtn = new Button { Content = "Clear All", Width = 80 };
            selectAllFilterPresetBtn.Click += SelectAllFilterPresets_Click;
            clearAllFilterPresetBtn.Click += ClearAllFilterPresets_Click;
            filterPresetButtonPanel.Children.Add(selectAllFilterPresetBtn);
            filterPresetButtonPanel.Children.Add(clearAllFilterPresetBtn);
            filterPresetContentPanel.Children.Add(filterPresetButtonPanel);

            if (DataContext is ApolloSyncSettingsViewModel)
            {
                UpdateFilterPresetCheckboxes(filterPresetPanel);
            }

            filterPresetExpander.Content = filterPresetContentPanel;
            stack.Children.Add(filterPresetExpander);

            // Excluded Filter Presets (collapsible)
            var excludedFilterPresetExpander = new Expander
            {
                Header = ResourceProvider.GetString("LOC_ApolloSync_Settings_ExcludedFilterPresets"),
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var excludedFilterPresetContentPanel = new StackPanel();

            excludedFilterPresetContentPanel.Children.Add(new TextBlock
            {
                Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_ExcludedFilterPresets_Help"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = System.Windows.Media.Brushes.Gray,
                FontStyle = FontStyles.Italic
            });

            var excludedFilterPresetBorder = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 0)
            };

            var excludedFilterPresetScrollViewer = new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var excludedFilterPresetPanel = new UniformGrid { Name = "ExcludedFilterPresetsPanel", Columns = 3 };
            excludedFilterPresetScrollViewer.Content = excludedFilterPresetPanel;
            excludedFilterPresetBorder.Child = excludedFilterPresetScrollViewer;
            excludedFilterPresetContentPanel.Children.Add(excludedFilterPresetBorder);

            var excludedFilterPresetButtonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var selectAllExcludedFilterPresetBtn = new Button { Content = "Select All", Width = 80, Margin = new Thickness(0, 0, 4, 0) };
            var clearAllExcludedFilterPresetBtn = new Button { Content = "Clear All", Width = 80 };
            selectAllExcludedFilterPresetBtn.Click += SelectAllExcludedFilterPresets_Click;
            clearAllExcludedFilterPresetBtn.Click += ClearAllExcludedFilterPresets_Click;
            excludedFilterPresetButtonPanel.Children.Add(selectAllExcludedFilterPresetBtn);
            excludedFilterPresetButtonPanel.Children.Add(clearAllExcludedFilterPresetBtn);
            excludedFilterPresetContentPanel.Children.Add(excludedFilterPresetButtonPanel);

            
            // Populate filter presets when DataContext is available
            if (DataContext is ApolloSyncSettingsViewModel)
            {
                UpdateExcludedFilterPresetCheckboxes(excludedFilterPresetPanel);
            }
            DataContextChanged += (s, e) =>
            {
                if (DataContext is ApolloSyncSettingsViewModel)
                {
                    UpdateFilterPresetCheckboxes(filterPresetPanel);
                    UpdateExcludedFilterPresetCheckboxes(excludedFilterPresetPanel);
                }
            };

            excludedFilterPresetExpander.Content = excludedFilterPresetContentPanel;
            stack.Children.Add(excludedFilterPresetExpander);

            outerScroll.Content = stack;
            return outerScroll;
        }

        private StackPanel BuildManageGamesTab()
        {
            var stack = new StackPanel { Margin = new Thickness(8) };

            // Header
            stack.Children.Add(new TextBlock { Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_ManageGames"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });

            // Games list with checkboxes for pinning and removing
            var gamesListBorder = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 8),
                MaxHeight = 300
            };

            var gamesScrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var gamesPanel = new StackPanel { Name = "ManagedGamesPanel" };
            _managedGamesPanel = gamesPanel;
            gamesScrollViewer.Content = gamesPanel;
            gamesListBorder.Child = gamesScrollViewer;
            stack.Children.Add(gamesListBorder);

            // Bulk action buttons
            var bulkActionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var refreshBtn = new Button { Content = "Refresh", Width = 80, Margin = new Thickness(0, 0, 4, 0) };
            var removeSelectedBtn = new Button { Content = "Remove Selected", Width = 120, Margin = new Thickness(0, 0, 4, 0) };
            var pinSelectedBtn = new Button { Content = "Pin Selected", Width = 100, Margin = new Thickness(0, 0, 4, 0) };
            var unpinSelectedBtn = new Button { Content = "Unpin Selected", Width = 110 };

            refreshBtn.Click += RefreshManagedGames_Click;
            removeSelectedBtn.Click += RemoveSelectedGames_Click;
            pinSelectedBtn.Click += PinSelectedGames_Click;
            unpinSelectedBtn.Click += UnpinSelectedGames_Click;

            bulkActionsPanel.Children.Add(refreshBtn);
            bulkActionsPanel.Children.Add(removeSelectedBtn);
            bulkActionsPanel.Children.Add(pinSelectedBtn);
            bulkActionsPanel.Children.Add(unpinSelectedBtn);
            stack.Children.Add(bulkActionsPanel);

            // Help text
            stack.Children.Add(new TextBlock { Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_ManageGames_Help"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

            // Populated from the Loaded handler — the DataContext is not assigned yet here.

            return stack;
        }

        #region Filter Preset Methods

        private void UpdateFilterPresetCheckboxes(Panel filterPresetPanel)
        {
            filterPresetPanel.Children.Clear();

            if (DataContext is ApolloSyncSettingsViewModel vm)
            {
                if (vm.AvailableFilterPresets == null || vm.AvailableFilterPresets.Count == 0)
                {
                    vm.RefreshFilterPresets();
                }

                if (vm.AvailableFilterPresets != null)
                {
                    if (vm.Settings.IncludedFilterPresetIds == null)
                    {
                        vm.Settings.IncludedFilterPresetIds = new List<Guid>();
                    }

                    foreach (var filterPreset in vm.AvailableFilterPresets)
                    {
                        var checkbox = new CheckBox
                        {
                            Content = filterPreset.Name,
                            Tag = filterPreset.Id,
                            Margin = new Thickness(0, 2, 0, 2)
                        };

                        checkbox.IsChecked = vm.Settings.IncludedFilterPresetIds.Contains(filterPreset.Id);

                        checkbox.Checked += (s, e) => OnFilterPresetCheckboxChanged(filterPreset.Id, true);
                        checkbox.Unchecked += (s, e) => OnFilterPresetCheckboxChanged(filterPreset.Id, false);

                        filterPresetPanel.Children.Add(checkbox);
                    }
                }
            }
        }

        private void SelectAllFilterPresets_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ApolloSyncSettingsViewModel vm && vm.AvailableFilterPresets != null)
            {
                vm.Settings.IncludedFilterPresetIds.Clear();
                foreach (var filterPreset in vm.AvailableFilterPresets)
                {
                    vm.Settings.IncludedFilterPresetIds.Add(filterPreset.Id);
                }
                UpdateFilterPresetCheckboxes(FindChild<UniformGrid>(this, "FilterPresetsPanel"));
            }
        }

        private void ClearAllFilterPresets_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ApolloSyncSettingsViewModel vm)
            {
                vm.Settings.IncludedFilterPresetIds.Clear();
                UpdateFilterPresetCheckboxes(FindChild<UniformGrid>(this, "FilterPresetsPanel"));
            }
        }

        private void OnFilterPresetCheckboxChanged(Guid presetId, bool isChecked)
        {
            if (DataContext is ApolloSyncSettingsViewModel vm)
            {
                if (vm.Settings.IncludedFilterPresetIds == null)
                {
                    vm.Settings.IncludedFilterPresetIds = new List<Guid>();
                }

                if (isChecked && !vm.Settings.IncludedFilterPresetIds.Contains(presetId))
                {
                    vm.Settings.IncludedFilterPresetIds.Add(presetId);
                }
                else if (!isChecked && vm.Settings.IncludedFilterPresetIds.Contains(presetId))
                {
                    vm.Settings.IncludedFilterPresetIds.Remove(presetId);
                }
            }
        }

        private void UpdateExcludedFilterPresetCheckboxes(Panel excludedFilterPresetPanel)
        {
            excludedFilterPresetPanel.Children.Clear();

            if (DataContext is ApolloSyncSettingsViewModel vm)
            {
                if (vm.AvailableFilterPresets == null || vm.AvailableFilterPresets.Count == 0)
                {
                    vm.RefreshFilterPresets();
                }

                if (vm.Settings.ExcludedFilterPresetIds == null)
                {
                    vm.Settings.ExcludedFilterPresetIds = new List<Guid>();
                }

                if (vm.AvailableFilterPresets != null)
                {
                    foreach (var filterPreset in vm.AvailableFilterPresets)
                    {
                        var checkbox = new CheckBox
                        {
                            Content = filterPreset.Name,
                            Tag = filterPreset.Id,
                            Margin = new Thickness(0, 2, 0, 2)
                        };

                        checkbox.IsChecked = vm.Settings.ExcludedFilterPresetIds.Contains(filterPreset.Id);

                        checkbox.Checked += (s, e) => OnExcludedFilterPresetCheckboxChanged(filterPreset.Id, true);
                        checkbox.Unchecked += (s, e) => OnExcludedFilterPresetCheckboxChanged(filterPreset.Id, false);

                        excludedFilterPresetPanel.Children.Add(checkbox);
                    }
                }
            }
        }

        private void SelectAllExcludedFilterPresets_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ApolloSyncSettingsViewModel vm && vm.AvailableFilterPresets != null)
            {
                if (vm.Settings.ExcludedFilterPresetIds == null)
                {
                    vm.Settings.ExcludedFilterPresetIds = new List<Guid>();
                }

                vm.Settings.ExcludedFilterPresetIds.Clear();
                foreach (var filterPreset in vm.AvailableFilterPresets)
                {
                    vm.Settings.ExcludedFilterPresetIds.Add(filterPreset.Id);
                }
                UpdateExcludedFilterPresetCheckboxes(FindChild<UniformGrid>(this, "ExcludedFilterPresetsPanel"));
            }
        }

        private void ClearAllExcludedFilterPresets_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ApolloSyncSettingsViewModel vm)
            {
                if (vm.Settings.ExcludedFilterPresetIds == null)
                {
                    vm.Settings.ExcludedFilterPresetIds = new List<Guid>();
                }

                vm.Settings.ExcludedFilterPresetIds.Clear();
                UpdateExcludedFilterPresetCheckboxes(FindChild<UniformGrid>(this, "ExcludedFilterPresetsPanel"));
            }
        }

        private void OnExcludedFilterPresetCheckboxChanged(Guid presetId, bool isChecked)
        {
            if (DataContext is ApolloSyncSettingsViewModel vm)
            {
                if (vm.Settings.ExcludedFilterPresetIds == null)
                {
                    vm.Settings.ExcludedFilterPresetIds = new List<Guid>();
                }

                if (isChecked && !vm.Settings.ExcludedFilterPresetIds.Contains(presetId))
                {
                    vm.Settings.ExcludedFilterPresetIds.Add(presetId);
                }
                else if (!isChecked && vm.Settings.ExcludedFilterPresetIds.Contains(presetId))
                {
                    vm.Settings.ExcludedFilterPresetIds.Remove(presetId);
                }
            }
        }

        #endregion

        private void BrowseAppsJsonPath_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = ResourceProvider.GetString("LOC_ApolloSync_Browse_Title"),
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = false
            };

            if (dlg.ShowDialog() == true)
            {
                if (DataContext is ApolloSyncSettingsViewModel vm)
                {
                    vm.Settings.AppsJsonPath = dlg.FileName;
                }
            }
        }

        // Manage Games Tab Methods
        private void RefreshManagedGames_Click(object sender, RoutedEventArgs e)
        {
            if (_managedGamesPanel != null)
            {
                RefreshManagedGamesList(_managedGamesPanel);
            }
        }

        private void RemoveSelectedGames_Click(object sender, RoutedEventArgs e)
        {
            if (_managedGamesPanel != null && DataContext is ApolloSyncSettingsViewModel vm)
            {
                var selectedGames = GetSelectedManagedGames(_managedGamesPanel);
                if (selectedGames.Any())
                {
                    // Off the UI thread: RemoveGamesFromManaged waits for any running sync and
                    // writes apps.json under _configLock, and a failed write dispatches a
                    // permission prompt back to this thread. Doing that synchronously here
                    // deadlocks.
                    System.Threading.Tasks.Task.Run(() => vm.RemoveGamesFromManaged(selectedGames))
                        .ContinueWith(_ => Dispatcher.BeginInvoke(new System.Action(() =>
                        {
                            if (_managedGamesPanel != null)
                            {
                                RefreshManagedGamesList(_managedGamesPanel);
                            }
                        })));
                }
            }
        }

        private void PinSelectedGames_Click(object sender, RoutedEventArgs e)
        {
            if (_managedGamesPanel != null && DataContext is ApolloSyncSettingsViewModel vm)
            {
                var selectedGames = GetSelectedManagedGames(_managedGamesPanel);
                foreach (var gameId in selectedGames)
                {
                    if (!vm.Settings.PinnedGameIds.Contains(gameId))
                    {
                        vm.Settings.PinnedGameIds.Add(gameId);
                    }
                }
                RefreshManagedGamesList(_managedGamesPanel);
            }
        }

        private void UnpinSelectedGames_Click(object sender, RoutedEventArgs e)
        {
            if (_managedGamesPanel != null && DataContext is ApolloSyncSettingsViewModel vm)
            {
                var selectedGames = GetSelectedManagedGames(_managedGamesPanel);
                foreach (var gameId in selectedGames)
                {
                    vm.Settings.PinnedGameIds.Remove(gameId);
                }
                RefreshManagedGamesList(_managedGamesPanel);
            }
        }

        private void RefreshManagedGamesList(StackPanel gamesPanel)
        {
            gamesPanel.Children.Clear();

            if (DataContext is ApolloSyncSettingsViewModel vm)
            {
                try
                {
                    var managedGames = vm.GetManagedGames();

                    if (managedGames.Count == 0)
                    {
                        var noGamesText = new TextBlock
                        {
                            Text = "No games are currently managed. Export games to see them here.",
                            TextAlignment = TextAlignment.Center,
                            Margin = new Thickness(8),
                            FontStyle = FontStyles.Italic
                        };
                        gamesPanel.Children.Add(noGamesText);
                        return;
                    }

                    foreach (var game in managedGames)
                    {
                        var gamePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

                        // Checkbox for selection
                        var checkbox = new CheckBox
                        {
                            Tag = game.Id,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0)
                        };
                        gamePanel.Children.Add(checkbox);

                        // Game name
                        var nameText = new TextBlock
                        {
                            Text = game.Name,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0)
                        };
                        gamePanel.Children.Add(nameText);

                        // Pinned indicator - make it more visible
                        if (vm.Settings.PinnedGameIds.Contains(game.Id))
                        {
                            var pinnedIcon = new TextBlock
                            {
                                Text = "📌 PINNED",
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(8, 0, 8, 0),
                                FontWeight = FontWeights.Bold,
                                Foreground = System.Windows.Media.Brushes.Orange,
                                ToolTip = "This game is pinned and will not be automatically removed"
                            };
                            gamePanel.Children.Add(pinnedIcon);
                        }

                        // Platform info if available
                        if (game.Platforms?.Count > 0)
                        {
                            var platformText = new TextBlock
                            {
                                Text = $"({string.Join(", ", game.Platforms.Select(p => p.Name))})",
                                VerticalAlignment = VerticalAlignment.Center,
                                FontStyle = FontStyles.Italic,
                                Foreground = System.Windows.Media.Brushes.Gray
                            };
                            gamePanel.Children.Add(platformText);
                        }

                        gamesPanel.Children.Add(gamePanel);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "ApolloSyncSettingsView.RefreshManagedGamesList: failed to load managed games list");
                    var errorText = new TextBlock
                    {
                        Text = $"Error loading managed games: {ex.Message}",
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(8),
                        Foreground = System.Windows.Media.Brushes.Red
                    };
                    gamesPanel.Children.Add(errorText);
                }
            }
        }

        private List<Guid> GetSelectedManagedGames(StackPanel gamesPanel)
        {
            var selectedGames = new List<Guid>();
            foreach (StackPanel gamePanel in gamesPanel.Children.OfType<StackPanel>())
            {
                var checkbox = gamePanel.Children.OfType<CheckBox>().FirstOrDefault();
                if (checkbox?.IsChecked == true && checkbox.Tag is Guid gameId)
                {
                    selectedGames.Add(gameId);
                }
            }
            return selectedGames;
        }

        private T FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent == null) return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T element && (string.IsNullOrEmpty(name) || element.Name == name))
                {
                    return element;
                }

                var foundChild = FindChild<T>(child, name);
                if (foundChild != null)
                {
                    return foundChild;
                }
            }

            return null;
        }

    }
}
