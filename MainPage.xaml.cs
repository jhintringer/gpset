using GPSet.Platforms.Android;
using Mapsui.Projections;

namespace GPSet;

public partial class MainPage : ContentPage
{
    private readonly Mapsui.UI.Objects.MyLocationLayer _myLocationLayer;
    private bool _coordinateTimerRunning;
    private bool _wasSimulating;
    private (double Latitude, double Longitude)? _lastRealPosition;

    public MainPage()
    {
        InitializeComponent();
        MapView.Map = MapFactory.CreateGoogleHybridMap();
        _myLocationLayer = MapView.MyLocationLayer;
        MapView.MyLocationEnabled = false;

        MockLocationService.StateChanged += OnSimulationStateChanged;
        MainActivity.CenterSimulationRequested += OnCenterSimulationRequested;
        Loaded += OnPageLoaded;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshSimulationState();
        StartCoordinateTimer();

        if (MainActivity.ConsumeCenterSimulationRequest())
            CenterOnSimulatedPosition();
    }

    protected override void OnDisappearing()
    {
        _coordinateTimerRunning = false;
        base.OnDisappearing();
    }

    private async void OnPageLoaded(object? sender, EventArgs e)
    {
        Loaded -= OnPageLoaded;

        try
        {
            var location = await GetBestDeviceLocationAsync(requestPermission: true);
            if (location is null || location.IsFromMockProvider)
                return;

            if (MockLocationService.IsStarting || MockLocationService.IsRunning)
            {
                RefreshSimulationState();
                return;
            }

            SetRealPosition(location);
            CenterMap(location.Latitude, location.Longitude, 4.8);
            UpdateCoordinates();
        }
        catch (Exception)
        {
            // The map remains available for manual position selection.
        }
    }

    private static async Task<Microsoft.Maui.Devices.Sensors.Location?>
        GetBestDeviceLocationAsync(bool requestPermission)
    {
        var permission = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (permission != PermissionStatus.Granted && requestPermission)
            permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

        if (permission != PermissionStatus.Granted)
            return null;

        Microsoft.Maui.Devices.Sensors.Location? location = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var request = new GeolocationRequest(GeolocationAccuracy.High);
            location = await Geolocation.Default.GetLocationAsync(request, timeout.Token);
        }
        catch (Exception)
        {
            // Fall back to the most recent device fix when a fresh fix times out.
        }

        return location ?? await Geolocation.Default.GetLastKnownLocationAsync();
    }

    private void StartCoordinateTimer()
    {
        if (_coordinateTimerRunning)
            return;

        _coordinateTimerRunning = true;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(100), () =>
        {
            if (!_coordinateTimerRunning)
                return false;

            UpdateCoordinates();
            return true;
        });
    }

    private (double Latitude, double Longitude) GetSelectedPosition()
    {
        var viewport = MapView.Map.Navigator.Viewport;
        var (longitude, latitude) = SphericalMercator.ToLonLat(
            viewport.CenterX, viewport.CenterY);
        return (latitude, longitude);
    }

    private void UpdateCoordinates()
    {
        if ((MockLocationService.IsStarting || MockLocationService.IsRunning) &&
            MockLocationService.MockLatitude is double latitude &&
            MockLocationService.MockLongitude is double longitude)
        {
            CoordinatesLabel.Text = $"{latitude:F6}, {longitude:F6}";
            return;
        }

        var position = GetSelectedPosition();
        CoordinatesLabel.Text = $"{position.Latitude:F6}, {position.Longitude:F6}";
    }

    private void SetRealPosition(Microsoft.Maui.Devices.Sensors.Location location)
    {
        _lastRealPosition = (location.Latitude, location.Longitude);
        UpdateLocationMarker(location.Latitude, location.Longitude);
    }

    private void UpdateLocationMarker(double latitude, double longitude)
    {
        _myLocationLayer.UpdateMyLocation(
            new Mapsui.UI.Maui.Position(latitude, longitude), false);
        MapView.MyLocationEnabled = true;
    }

    private void CenterMap(double latitude, double longitude, double resolution)
    {
        var (x, y) = SphericalMercator.FromLonLat(longitude, latitude);
        MapView.Map.Navigator.CenterOnAndZoomTo(new Mapsui.MPoint(x, y), resolution);
    }

    private void CenterOnSimulatedPosition()
    {
        if (MockLocationService.MockLatitude is not double latitude ||
            MockLocationService.MockLongitude is not double longitude)
            return;

        UpdateLocationMarker(latitude, longitude);
        double resolution = MapView.Map.Navigator.Viewport.Resolution;
        CenterMap(latitude, longitude, resolution > 0 ? resolution : 4.8);
    }

    private async Task RefreshRealPositionAfterSimulationAsync()
    {
        await Task.Delay(750);
        if (MockLocationService.IsStarting || MockLocationService.IsRunning)
            return;

        try
        {
            var location = await GetBestDeviceLocationAsync(requestPermission: false);
            if (location is not null && !location.IsFromMockProvider &&
                !MockLocationService.IsStarting && !MockLocationService.IsRunning)
                SetRealPosition(location);
        }
        catch (Exception)
        {
            // Keep the last real fix when a fresh post-simulation fix is unavailable.
        }
    }

    private async void OnSimulationButtonClicked(object? sender, EventArgs e)
    {
        if (MockLocationService.IsStarting)
            return;

        if (MockLocationService.IsRunning)
        {
            MockLocationService.Stop();
            RefreshSimulationState();
            return;
        }

        if (!MockLocationService.CanMockLocations())
        {
            bool openSettings = await DisplayAlertAsync(
                "Select GPSet as mock location app",
                "Open Android Developer options, choose ‘Select mock location app’, and select GPSet.",
                "OPEN SETTINGS", "CANCEL");
            if (openSettings)
                MockLocationService.OpenDeveloperOptions();
            return;
        }

        if (!MockLocationService.IsSystemLocationEnabled())
        {
            bool openSettings = await DisplayAlertAsync(
                "Turn on Android Location",
                "Android Location must be enabled before a mock GPS position can be delivered.",
                "OPEN SETTINGS", "CANCEL");
            if (openSettings)
                MockLocationService.OpenLocationSettings();
            return;
        }

        var locationPermission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (locationPermission != PermissionStatus.Granted)
        {
            await DisplayAlertAsync("Location permission required",
                "Android requires location permission for the foreground simulation service.", "OK");
            return;
        }

        await Permissions.RequestAsync<Permissions.PostNotifications>();
        var position = GetSelectedPosition();
        MockLocationService.Start(position.Latitude, position.Longitude);
        RefreshSimulationState();
    }

    private void OnSimulationStateChanged(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(RefreshSimulationState);

    private void OnCenterSimulationRequested(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(() =>
        {
            if (MainActivity.ConsumeCenterSimulationRequest())
                CenterOnSimulatedPosition();
        });

    private void RefreshSimulationState()
    {
        bool running = MockLocationService.IsRunning;
        bool starting = MockLocationService.IsStarting;
        bool simulating = running || starting;

        Crosshair.IsVisible = !simulating;
        Crosshair.TranslationX = 0;
        Crosshair.TranslationY = 0;

        if (simulating &&
            MockLocationService.MockLatitude is double latitude &&
            MockLocationService.MockLongitude is double longitude)
        {
            UpdateLocationMarker(latitude, longitude);
        }
        else if (_wasSimulating)
        {
            if (_lastRealPosition is { } realPosition)
                UpdateLocationMarker(realPosition.Latitude, realPosition.Longitude);
            else
                MapView.MyLocationEnabled = false;

            _ = RefreshRealPositionAfterSimulationAsync();
        }

        _wasSimulating = simulating;
        SimulationButton.IsEnabled = !starting;
        SimulationButton.Text = starting ? "STARTING…" : running ? "STOP" : "START";
        SimulationButton.BackgroundColor = Color.FromArgb(running ? "#C62828" : "#2E7D32");
        UpdateCoordinates();
    }
}
