# GPSet

GPSet is an Android-only .NET 10 MAUI developer utility that publishes the fixed
center of a Mapsui satellite map as Android's mock GPS position.

## Run

1. Enable **Developer options** on the Android device or emulator.
2. Install or reinstall the current GPSet APK so Android reads its mock-location
   permission declaration.
3. Open **Select mock location app** and select **GPSet**.
4. Build and deploy with `dotnet build -f net10.0-android` or Visual Studio.
5. Pan/zoom the map until the crosshair is over the desired point, then tap
   **START**. Tap **STOP** to remove the mock providers and ongoing notification.

Android may request location and notification permissions when simulation starts.
The location foreground-service permission is required by current Android versions.

## Map imagery

The app uses Mapsui 5.1.0 with Google's hybrid (`lyrs=y`) tile endpoint,
combining satellite imagery with road and place labels, and shows Google attribution.
That endpoint is not a documented, supported Google Maps Platform tile API and can
change or be blocked. Before distributing this app, replace
`MapFactory.GoogleHybridUrl` with a licensed/supported tile source and comply with
that provider's terms, authentication, caching, and attribution requirements.
