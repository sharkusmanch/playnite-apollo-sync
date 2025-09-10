using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace ApolloSync
{
    public partial class ApolloSyncSettingsView : UserControl
    {
        public ApolloSyncSettingsView()
        {
            BuildUI();

            // Auto-load managed games when the settings window opens
            Loaded += (sender, e) =>
            {
                // Wait for DataContext to be set, then refresh managed games
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    if (DataContext != null)
                    {
                        var manageGamesTab = FindChild<TabItem>("ManageGamesTab");
                        if (manageGamesTab != null)
                        {
                            var gamesPanel = FindChild<StackPanel>("ManagedGamesPanel");
                            if (gamesPanel != null)
                            {
                                RefreshManagedGamesList(gamesPanel);
                            }
                        }
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

            // Notifications header
            var notificationsHeader = new TextBlock
            {
                Text = "Notifications:",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 8, 0, 8)
            };
            stack.Children.Add(notificationsHeader);

            // Show notifications
            var chkShowNotifications = new CheckBox { Content = "Show sync notifications", Margin = new Thickness(20, 0, 0, 4) };
            chkShowNotifications.SetBinding(CheckBox.IsCheckedProperty, new Binding("Settings.ShowNotifications") { Mode = BindingMode.TwoWay });
            stack.Children.Add(chkShowNotifications);

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

        private StackPanel BuildFiltersTab()
        {
            var stack = new StackPanel { Margin = new Thickness(8) };

            // Platform Filters (collapsible)
            var platformExpander = new Expander
            {
                Header = ResourceProvider.GetString("LOC_ApolloSync_Settings_IncludedPlatforms"),
                IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var platformContentPanel = new StackPanel();

            // Create platform selection table
            var platformsBorder = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 0)
            };

            var platformsScrollViewer = new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var platformsPanel = new StackPanel { Name = "PlatformsPanel" };
            platformsScrollViewer.Content = platformsPanel;
            platformsBorder.Child = platformsScrollViewer;
            platformContentPanel.Children.Add(platformsBorder);

            // Add "Select All" and "Clear All" buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var selectAllBtn = new Button { Content = "Select All", Width = 80, Margin = new Thickness(0, 0, 4, 0) };
            var clearAllBtn = new Button { Content = "Clear All", Width = 80 };
            // Platform filtering disabled - using filter presets only
            // selectAllBtn.Click += SelectAllPlatforms_Click;
            // clearAllBtn.Click += ClearAllPlatforms_Click;
            buttonPanel.Children.Add(selectAllBtn);
            buttonPanel.Children.Add(clearAllBtn);
            platformContentPanel.Children.Add(buttonPanel);
            platformContentPanel.Children.Add(new TextBlock { Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_IncludedPlatforms_Help"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

            // Platform filtering disabled - using filter presets only
            // if (DataContext is ApolloSyncSettingsViewModel vm)
            // {
            //     UpdatePlatformCheckboxes(platformsPanel);
            // }
            // DataContextChanged += (s, e) =>
            // {
            //     if (DataContext is ApolloSyncSettingsViewModel viewModel)
            //     {
            //         UpdatePlatformCheckboxes(platformsPanel);
            //     }
            // };

            platformExpander.Content = platformContentPanel;
            platformExpander.Visibility = Visibility.Collapsed; // TEMP: Disable custom filters
            stack.Children.Add(platformExpander);

            // Label Filter (collapsible)
            var labelExpander = new Expander
            {
                Header = ResourceProvider.GetString("LOC_ApolloSync_Settings_RequiredLabel"),
                IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var labelContentPanel = new StackPanel();
            var labelCombo = new ComboBox { MinWidth = 200, Margin = new Thickness(0, 4, 0, 0) };
            labelCombo.SetBinding(ComboBox.ItemsSourceProperty, new Binding("LabelOptions"));
            labelCombo.SetBinding(ComboBox.SelectedValueProperty, new Binding("Settings.RequiredLabelId") { Mode = BindingMode.TwoWay });
            labelCombo.DisplayMemberPath = "Name";
            labelCombo.SelectedValuePath = "Id";
            labelContentPanel.Children.Add(labelCombo);

            var btnCreateLabel = new Button
            {
                Content = ResourceProvider.GetString("LOC_ApolloSync_Settings_CreateDefaultLabel"),
                Width = 150,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
            };
            btnCreateLabel.Click += CreateDefaultLabel_Click;
            labelContentPanel.Children.Add(btnCreateLabel);
            labelContentPanel.Children.Add(new TextBlock { Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_RequiredLabel_Help"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

            labelExpander.Content = labelContentPanel;
            labelExpander.Visibility = Visibility.Collapsed; // TEMP: Disable custom filters
            stack.Children.Add(labelExpander);

            // Completion Status Filter (collapsible)
            var completionExpander = new Expander
            {
                Header = ResourceProvider.GetString("LOC_ApolloSync_Settings_CompletionStatusFilter"),
                IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var completionContentPanel = new StackPanel();

            // Create completion status selection table
            var completionBorder = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 0)
            };

            var completionScrollViewer = new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var completionPanel = new StackPanel { Name = "CompletionPanel" };
            completionScrollViewer.Content = completionPanel;
            completionBorder.Child = completionScrollViewer;
            completionContentPanel.Children.Add(completionBorder);

            // Add "Select All" and "Clear All" buttons for completion status
            var completionButtonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var selectAllCompletionBtn = new Button { Content = "Select All", Width = 80, Margin = new Thickness(0, 0, 4, 0) };
            var clearAllCompletionBtn = new Button { Content = "Clear All", Width = 80 };
            // Completion status filtering disabled - using filter presets only
            // selectAllCompletionBtn.Click += SelectAllCompletionStatuses_Click;
            // clearAllCompletionBtn.Click += ClearAllCompletionStatuses_Click;
            completionButtonPanel.Children.Add(selectAllCompletionBtn);
            completionButtonPanel.Children.Add(clearAllCompletionBtn);
            completionContentPanel.Children.Add(completionButtonPanel);
            completionContentPanel.Children.Add(new TextBlock { Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_CompletionStatusFilter_Help"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

            // Completion status filtering disabled - using filter presets only
            // if (DataContext is ApolloSyncSettingsViewModel vm2)
            // {
            //     UpdateCompletionStatusCheckboxes(completionPanel);
            // }
            // DataContextChanged += (s, e) =>
            // {
            //     if (DataContext is ApolloSyncSettingsViewModel viewModel2)
            //     {
            //         UpdateCompletionStatusCheckboxes(completionPanel);
            //     }
            // };

            completionExpander.Content = completionContentPanel;
            completionExpander.Visibility = Visibility.Collapsed; // TEMP: Disable custom filters
            stack.Children.Add(completionExpander);

            // Filter Presets (collapsible)
            var filterPresetExpander = new Expander
            {
                Header = "Filter Presets",
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var filterPresetContentPanel = new StackPanel();

            // Create filter preset selection table
            var filterPresetBorder = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 0)
            };

            var filterPresetScrollViewer = new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var filterPresetPanel = new StackPanel { Name = "FilterPresetsPanel" };
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

            // Help text
            filterPresetContentPanel.Children.Add(new TextBlock
            {
                Text = "Select one or more filter presets. Games that match ANY selected preset will be eligible for export (OR logic). Create filter presets in Playnite's main library view first.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = System.Windows.Media.Brushes.Gray,
                FontStyle = FontStyles.Italic
            });

            // Populate filter presets when DataContext is available
            if (DataContext is ApolloSyncSettingsViewModel vm4)
            {
                UpdateFilterPresetCheckboxes(filterPresetPanel);
            }
            DataContextChanged += (s, e) =>
            {
                if (DataContext is ApolloSyncSettingsViewModel viewModel4)
                {
                    UpdateFilterPresetCheckboxes(filterPresetPanel);
                }
            };

            filterPresetExpander.Content = filterPresetContentPanel;
            stack.Children.Add(filterPresetExpander);

            // Category Filter (collapsible)
            var categoryExpander = new Expander
            {
                Header = ResourceProvider.GetString("LOC_ApolloSync_Settings_CategoryFilter"),
                IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var categoryContentPanel = new StackPanel();

            // Create category selection table
            var categoryBorder = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 0)
            };

            var categoryScrollViewer = new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var categoryPanel = new StackPanel { Name = "CategoriesPanel" };
            categoryScrollViewer.Content = categoryPanel;
            categoryBorder.Child = categoryScrollViewer;
            categoryContentPanel.Children.Add(categoryBorder);

            // Add "Select All" and "Clear All" buttons for categories
            var categoryButtonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var selectAllCategoryBtn = new Button { Content = "Select All", Width = 80, Margin = new Thickness(0, 0, 4, 0) };
            var clearAllCategoryBtn = new Button { Content = "Clear All", Width = 80 };
            // Category filtering disabled - using filter presets only
            // selectAllCategoryBtn.Click += SelectAllCategories_Click;
            // clearAllCategoryBtn.Click += ClearAllCategories_Click;
            categoryButtonPanel.Children.Add(selectAllCategoryBtn);
            categoryButtonPanel.Children.Add(clearAllCategoryBtn);
            categoryContentPanel.Children.Add(categoryButtonPanel);
            categoryContentPanel.Children.Add(new TextBlock { Text = ResourceProvider.GetString("LOC_ApolloSync_Settings_CategoryFilter_Help"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

            // Category filtering disabled - using filter presets only
            // if (DataContext is ApolloSyncSettingsViewModel vm3)
            // {
            //     UpdateCategoryCheckboxes(categoryPanel);
            // }
            // DataContextChanged += (s, e) =>
            // {
            //     if (DataContext is ApolloSyncSettingsViewModel viewModel3)
            //     {
            //         UpdateCategoryCheckboxes(categoryPanel);
            //     }
            // };

            categoryExpander.Content = categoryContentPanel;
            categoryExpander.Visibility = Visibility.Collapsed; // TEMP: Disable custom filters
            stack.Children.Add(categoryExpander);

            return stack;
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

            // Populate games list when tab is created
            RefreshManagedGamesList(gamesPanel);

            return stack;
        }

        #region Filter Preset Methods

        private void UpdateFilterPresetCheckboxes(StackPanel filterPresetPanel)
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
                UpdateFilterPresetCheckboxes(FindChild<StackPanel>(this, "FilterPresetsPanel"));
            }
        }

        private void ClearAllFilterPresets_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ApolloSyncSettingsViewModel vm)
            {
                vm.Settings.IncludedFilterPresetIds.Clear();
                UpdateFilterPresetCheckboxes(FindChild<StackPanel>(this, "FilterPresetsPanel"));
            }
        }

        private void OnFilterPresetCheckboxChanged(Guid presetId, bool isChecked)
        {
            if (DataContext is ApolloSyncSettingsViewModel vm)
            {
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

        private void CreateDefaultLabel_Click(object sender, RoutedEventArgs e)
        {
            // Default label creation disabled - using filter presets only
            // if (DataContext is ApolloSyncSettingsViewModel vm)
            // {
            //     vm.CreateDefaultLabel();
            // }
        }

        // Manage Games Tab Methods
        private void RefreshManagedGames_Click(object sender, RoutedEventArgs e)
        {
            var gamesPanel = FindChild<StackPanel>("ManagedGamesPanel");
            if (gamesPanel != null)
            {
                RefreshManagedGamesList(gamesPanel);
            }
        }

        private void RemoveSelectedGames_Click(object sender, RoutedEventArgs e)
        {
            var gamesPanel = FindChild<StackPanel>("ManagedGamesPanel");
            if (gamesPanel != null && DataContext is ApolloSyncSettingsViewModel vm)
            {
                var selectedGames = GetSelectedManagedGames(gamesPanel);
                if (selectedGames.Any())
                {
                    vm.RemoveGamesFromManaged(selectedGames);
                    RefreshManagedGamesList(gamesPanel);
                }
            }
        }

        private void PinSelectedGames_Click(object sender, RoutedEventArgs e)
        {
            var gamesPanel = FindChild<StackPanel>("ManagedGamesPanel");
            if (gamesPanel != null && DataContext is ApolloSyncSettingsViewModel vm)
            {
                var selectedGames = GetSelectedManagedGames(gamesPanel);
                foreach (var gameId in selectedGames)
                {
                    if (!vm.Settings.PinnedGameIds.Contains(gameId))
                    {
                        vm.Settings.PinnedGameIds.Add(gameId);
                    }
                }
                RefreshManagedGamesList(gamesPanel);
            }
        }

        private void UnpinSelectedGames_Click(object sender, RoutedEventArgs e)
        {
            var gamesPanel = FindChild<StackPanel>("ManagedGamesPanel");
            if (gamesPanel != null && DataContext is ApolloSyncSettingsViewModel vm)
            {
                var selectedGames = GetSelectedManagedGames(gamesPanel);
                foreach (var gameId in selectedGames)
                {
                    vm.Settings.PinnedGameIds.Remove(gameId);
                }
                RefreshManagedGamesList(gamesPanel);
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

        private T FindChild<T>(string name) where T : FrameworkElement
        {
            return FindChild<T>(this, name);
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
