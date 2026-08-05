using GPSet.Platforms.Android;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using NetTopologySuite.Geometries;

namespace GPSet;

public partial class MainPage : ContentPage
{
    private enum TravelMode
    {
        Walking,
        Running,
        Driving
    }

    private readonly Mapsui.UI.Objects.MyLocationLayer _myLocationLayer;
    private readonly MemoryLayer _routeLayer;
    private readonly List<GeoPosition> _waypoints = [];
    private TravelMode _travelMode = TravelMode.Walking;
    private bool _coordinateTimerRunning;
    private bool _crosshairSuppressed;
    private bool _routeEditing;
    private bool _wasSimulating;
    private bool _wasWalking;
    private GeoPosition? _lockedPosition;
    private GeoPosition? _lastRealPosition;

    public MainPage()
    {
        InitializeComponent();
        MapView.Map = MapFactory.CreateGoogleHybridMap();
        _myLocationLayer = MapView.MyLocationLayer;
        _routeLayer = new MemoryLayer
        {
            Name = "Walking route",
            Style = new VectorStyle
            {
                Line = new Pen(Mapsui.Styles.Color.FromString("#FF6D00"), 5)
            }
        };

        MapView.Map.Layers.Remove(_myLocationLayer);
        MapView.Map.Layers.Add(_routeLayer);
        MapView.Map.Layers.Add(_myLocationLayer);
        MapView.MyLocationEnabled = false;

        MockLocationService.StateChanged += OnSimulationStateChanged;
        MockLocationService.PositionChanged += OnSimulationPositionChanged;
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
            var permission = await EnsureLocationPermissionAsync(requestPermission: true);
            if (permission != PermissionStatus.Granted)
                return;

            Microsoft.Maui.Devices.Sensors.Location? cachedLocation = null;
            try
            {
                cachedLocation = await Geolocation.Default.GetLastKnownLocationAsync();
            }
            catch (Exception)
            {
                // Continue with a fresh fix when Android has no readable cache.
            }

            GeoPosition? initialPosition = null;
            if (IsUsableRealLocation(cachedLocation, TimeSpan.FromMinutes(15)) &&
                !IsSimulationActive())
            {
                SetRealPosition(cachedLocation!);
                CenterMap(cachedLocation!.Latitude, cachedLocation.Longitude, 4.8);
                UpdateCoordinates();
                initialPosition = new GeoPosition(
                    cachedLocation.Latitude, cachedLocation.Longitude);
            }

            var freshLocation = await GetFreshDeviceLocationAsync(
                TimeSpan.FromSeconds(6));
            if (!IsUsableRealLocation(freshLocation, TimeSpan.FromMinutes(2)) ||
                IsSimulationActive())
                return;

            bool keepCamera = initialPosition is { } shownPosition &&
                !IsMapCenteredNear(shownPosition);
            SetRealPosition(freshLocation!);
            if (!keepCamera)
            {
                CenterMap(freshLocation!.Latitude, freshLocation.Longitude, 4.8);
                UpdateCoordinates();
            }
        }
        catch (Exception)
        {
            // The map remains available for manual position selection.
        }
    }

    private static async Task<PermissionStatus> EnsureLocationPermissionAsync(
        bool requestPermission)
    {
        var permission = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (permission != PermissionStatus.Granted && requestPermission)
            permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        return permission;
    }

    private static async Task<Microsoft.Maui.Devices.Sensors.Location?>
        GetFreshDeviceLocationAsync(TimeSpan timeoutDuration)
    {
        try
        {
            using var timeout = new CancellationTokenSource(timeoutDuration);
            var request = new GeolocationRequest(GeolocationAccuracy.High);
            return await Geolocation.Default.GetLocationAsync(request, timeout.Token);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<Microsoft.Maui.Devices.Sensors.Location?>
        GetBestDeviceLocationAsync(bool requestPermission)
    {
        var permission = await EnsureLocationPermissionAsync(requestPermission);
        if (permission != PermissionStatus.Granted)
            return null;

        var freshLocation = await GetFreshDeviceLocationAsync(TimeSpan.FromSeconds(6));
        if (IsUsableRealLocation(freshLocation, TimeSpan.FromMinutes(2)))
            return freshLocation;

        try
        {
            var cachedLocation = await Geolocation.Default.GetLastKnownLocationAsync();
            return IsUsableRealLocation(cachedLocation, TimeSpan.FromMinutes(15))
                ? cachedLocation
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsUsableRealLocation(
        Microsoft.Maui.Devices.Sensors.Location? location, TimeSpan maximumAge)
    {
        if (location is null || location.IsFromMockProvider ||
            !double.IsFinite(location.Latitude) || location.Latitude is < -90 or > 90 ||
            !double.IsFinite(location.Longitude) || location.Longitude is < -180 or > 180)
            return false;

        if (location.Accuracy is double accuracy &&
            (!double.IsFinite(accuracy) || accuracy > 5_000))
            return false;

        if (location.Timestamp == default)
            return true;

        TimeSpan age = DateTimeOffset.UtcNow - location.Timestamp;
        return age >= TimeSpan.FromMinutes(-1) && age <= maximumAge;
    }

    private static bool IsSimulationActive() =>
        MockLocationService.IsStarting || MockLocationService.IsRunning;

    private bool IsMapCenteredNear(GeoPosition position)
    {
        var viewport = MapView.Map.Navigator.Viewport;
        var (x, y) = SphericalMercator.FromLonLat(
            position.Longitude, position.Latitude);
        double tolerance = Math.Max(2, viewport.Resolution * 12);
        return Math.Abs(viewport.CenterX - x) <= tolerance &&
            Math.Abs(viewport.CenterY - y) <= tolerance;
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

            DetectRouteEditing();
            UpdateCoordinates();
            return true;
        });
    }

    private GeoPosition GetSelectedPosition()
    {
        var viewport = MapView.Map.Navigator.Viewport;
        var (longitude, latitude) = SphericalMercator.ToLonLat(
            viewport.CenterX, viewport.CenterY);
        return new GeoPosition(latitude, longitude);
    }

    private void DetectRouteEditing()
    {
        if (MockLocationService.IsWalking || !MockLocationService.IsRunning ||
            _lockedPosition is not { } locked ||
            (_routeEditing && !_crosshairSuppressed))
            return;

        var viewport = MapView.Map.Navigator.Viewport;
        var (lockedX, lockedY) = SphericalMercator.FromLonLat(
            locked.Longitude, locked.Latitude);
        double distance = Math.Sqrt(
            Math.Pow(viewport.CenterX - lockedX, 2) +
            Math.Pow(viewport.CenterY - lockedY, 2));
        double dragThreshold = Math.Max(2, viewport.Resolution * 4);

        if (distance <= dragThreshold)
            return;

        _routeEditing = true;
        _crosshairSuppressed = false;
        RefreshSimulationState();
    }

    private void UpdateCoordinates()
    {
        if (!_routeEditing || MockLocationService.IsWalking)
        {
            if ((MockLocationService.IsStarting || MockLocationService.IsRunning) &&
                MockLocationService.MockLatitude is double latitude &&
                MockLocationService.MockLongitude is double longitude)
            {
                CoordinatesLabel.Text = $"{latitude:F6}, {longitude:F6}";
                return;
            }
        }

        var position = GetSelectedPosition();
        CoordinatesLabel.Text = $"{position.Latitude:F6}, {position.Longitude:F6}";
    }

    private void SetRealPosition(Microsoft.Maui.Devices.Sensors.Location location)
    {
        _lastRealPosition = new GeoPosition(location.Latitude, location.Longitude);
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
            if (MockLocationService.IsWalking)
                MockLocationService.StopWalking();
            MockLocationService.Stop();
            _routeEditing = false;
            _crosshairSuppressed = false;
            _lockedPosition = null;
            _waypoints.Clear();
            RefreshRouteLayer();
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
        _lockedPosition = position;
        _crosshairSuppressed = false;
        MockLocationService.Start(position.Latitude, position.Longitude);
        RefreshSimulationState();
    }

    private void OnWaypointButtonClicked(object? sender, EventArgs e)
    {
        if (MockLocationService.IsWalking)
            return;

        _waypoints.Add(GetSelectedPosition());
        RefreshRouteLayer();
        RefreshSimulationState();
    }

    private void OnUndoButtonClicked(object? sender, EventArgs e)
    {
        if (MockLocationService.IsWalking || _waypoints.Count == 0)
            return;

        _waypoints.RemoveAt(_waypoints.Count - 1);
        if (_waypoints.Count == 0)
        {
            _routeEditing = false;
            _crosshairSuppressed = true;
            if (MockLocationService.MockLatitude is double latitude &&
                MockLocationService.MockLongitude is double longitude)
            {
                _lockedPosition = new GeoPosition(latitude, longitude);
                double resolution = MapView.Map.Navigator.Viewport.Resolution;
                CenterMap(latitude, longitude, resolution > 0 ? resolution : 4.8);
            }
        }

        RefreshRouteLayer();
        RefreshSimulationState();
    }

    private void OnPlayButtonClicked(object? sender, EventArgs e)
    {
        if (MockLocationService.IsWalking || _waypoints.Count == 0 ||
            MockLocationService.MockLatitude is not double latitude ||
            MockLocationService.MockLongitude is not double longitude)
            return;

        _lockedPosition = new GeoPosition(latitude, longitude);
        _travelMode = TravelMode.Walking;
        MockLocationService.StartRoute(
            _lockedPosition.Value, _waypoints, GetTravelSpeed());
        RefreshSimulationState();
    }

    private void OnSpeedButtonClicked(object? sender, EventArgs e)
    {
        _travelMode = _travelMode switch
        {
            TravelMode.Walking => TravelMode.Running,
            TravelMode.Running => TravelMode.Driving,
            _ => TravelMode.Walking
        };

        MockLocationService.SetWalkingSpeed(GetTravelSpeed());
        SpeedButton.Text = _travelMode.ToString();
    }

    private double GetTravelSpeed() => _travelMode switch
    {
        TravelMode.Walking => 1.4,
        TravelMode.Running => 3.0,
        TravelMode.Driving => 13.9,
        _ => 1.4
    };

    private void OnStopButtonClicked(object? sender, EventArgs e)
    {
        if (!MockLocationService.IsWalking)
            return;

        MockLocationService.StopWalking();
        _routeEditing = true;
        _crosshairSuppressed = true;
        if (MockLocationService.MockLatitude is double latitude &&
            MockLocationService.MockLongitude is double longitude)
        {
            _lockedPosition = new GeoPosition(latitude, longitude);
            double resolution = MapView.Map.Navigator.Viewport.Resolution;
            CenterMap(latitude, longitude, resolution > 0 ? resolution : 4.8);
        }
        RefreshRouteLayer();
        RefreshSimulationState();
    }

    private void OnSimulationStateChanged(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(RefreshSimulationState);

    private void OnSimulationPositionChanged(
        object? sender, MockPositionChangedEventArgs e) =>
        Dispatcher.Dispatch(() =>
        {
            _lockedPosition = new GeoPosition(e.Latitude, e.Longitude);
            UpdateLocationMarker(e.Latitude, e.Longitude);

            for (int i = 0; i < e.PassedWaypoints && _waypoints.Count > 0; i++)
                _waypoints.RemoveAt(0);

            RefreshRouteLayer();
            RefreshSimulationState();
        });

    private void OnCenterSimulationRequested(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(() =>
        {
            if (MainActivity.ConsumeCenterSimulationRequest())
                CenterOnSimulatedPosition();
        });

    private void RefreshRouteLayer()
    {
        if (_lockedPosition is not { } start || _waypoints.Count == 0)
        {
            _routeLayer.Features = [];
            _routeLayer.DataHasChanged();
            return;
        }

        var coordinates = new List<Coordinate>(_waypoints.Count + 1);
        AddProjectedCoordinate(coordinates, start);
        foreach (var waypoint in _waypoints)
            AddProjectedCoordinate(coordinates, waypoint);

        _routeLayer.Features =
        [
            new GeometryFeature
            {
                Geometry = new LineString(coordinates.ToArray())
            }
        ];
        _routeLayer.DataHasChanged();
    }

    private static void AddProjectedCoordinate(
        ICollection<Coordinate> coordinates, GeoPosition position)
    {
        var (x, y) = SphericalMercator.FromLonLat(
            position.Longitude, position.Latitude);
        coordinates.Add(new Coordinate(x, y));
    }

    private void RefreshSimulationState()
    {
        bool running = MockLocationService.IsRunning;
        bool starting = MockLocationService.IsStarting;
        bool walking = MockLocationService.IsWalking;
        bool simulating = running || starting;

        if (simulating &&
            MockLocationService.MockLatitude is double latitude &&
            MockLocationService.MockLongitude is double longitude)
        {
            _lockedPosition = new GeoPosition(latitude, longitude);
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

        bool walkingJustStopped = _wasWalking && !walking && running;
        if (!simulating)
        {
            _routeEditing = false;
            _crosshairSuppressed = false;
        }
        else if (walkingJustStopped)
        {
            _routeEditing = _waypoints.Count > 0;
            _crosshairSuppressed = true;
        }

        if (walkingJustStopped && _lockedPosition is { } stoppedPosition)
        {
            double resolution = MapView.Map.Navigator.Viewport.Resolution;
            CenterMap(stoppedPosition.Latitude, stoppedPosition.Longitude,
                resolution > 0 ? resolution : 4.8);
        }

        Crosshair.IsVisible = !simulating ||
            (_routeEditing && !walking && !_crosshairSuppressed);
        Crosshair.TranslationX = 0;
        Crosshair.TranslationY = 0;

        RouteControls.IsVisible = simulating && _routeEditing;
        SimulationButton.IsVisible = true;
        SimulationButton.IsEnabled = !starting;
        SimulationButton.Text = starting ? "STARTING…" : running ? "End" : "Simulate";
        SimulationButton.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb(
            running ? "#C62828" : "#2E7D32");

        WaypointButton.IsVisible = !walking;
        WaypointButton.IsEnabled = !walking;
        UndoButton.IsVisible = _waypoints.Count > 0 && !walking;
        UndoButton.IsEnabled = !walking;
        PlayButton.IsVisible = _waypoints.Count > 0 && !walking;
        SpeedButton.IsVisible = walking;
        SpeedButton.Text = _travelMode.ToString();
        StopButton.IsVisible = walking;

        _wasSimulating = simulating;
        _wasWalking = walking;
        UpdateCoordinates();
    }
}
