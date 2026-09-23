using Xunit;

// Disable parallel test execution within AvasRoutingApp.Tests because multiple test classes
// instantiate WPF UI elements (Application, MainWindow, SettingsDialog, WebView2) on STA threads.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
