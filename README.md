# Lunar Mission Planner

Lunar Mission Planner is an interactive Unity application for exploring lunar terrain and comparing potential landing sites. It combines NASA LOLA topography with a time-dependent Sun, Earth, and Moon model to show terrain horizons, celestial visibility, and estimated solar-panel output.

The project is organized into three modules: lunar coordinates and terrain, celestial motion, and site visibility and power. It is intended for visualization and early analysis.

## Features

| Module | What it does |
| --- | --- |
| **Site and terrain** | Converts planetocentric latitude and east-positive longitude to the LOLA lunar frame, samples elevation from tiled terrain data, and displays selected locations on the Moon. A site can be placed by entering coordinates or clicking the Moon. |
| **Celestial motion** | Loads a precomputed JPL DE440s ephemeris cache, provides a UTC simulation clock, and animates the Sun, Earth, and Moon. The clock starts at the current UTC time and is paused initially. |
| **Visibility and power** | Builds a terrain horizon for each site, evaluates Sun-disc visibility and Earth line of sight, and estimates panel irradiance, power, and accumulated energy. |

The UI supports two independent sites, A and B. Each has its own location, marker, terrain horizon, panel settings, readouts, and energy history. The comparison plot can show hourly UTC histories for visibility, angles, panel output, and energy. Exported data is CSV and is saved to the user's `Downloads` folder.

The scientific calculations use double-precision values in physical units and the lunar Mean Earth/Polar Axis frame. Unity transforms, meshes, lighting, and camera positions are used for display and interaction; they are not inputs to the scientific visibility calculations.

## Requirements

- Unity Editor **6000.6.7f1** (Unity 6), using the packages listed in `Packages/manifest.json`.
- Git and **Git LFS**. Large project data and models are stored through Git LFS.

## Getting started

Install Git LFS before cloning so the repository's large assets are downloaded instead of leaving LFS pointer files in their place:

```bash
git lfs install
git clone https://github.com/Kritsos/LunarMissionPlanner.git
cd Lunar-Space-Mission
git lfs pull
```

Then add the repository root as a project in Unity Hub, open it with Unity `6000.6.7f1`, and open `Assets/Scenes/SolarSystem.unity`. Press **Play** to run the application. The scene is also listed in `ProjectSettings/EditorBuildSettings.asset`.

The runtime terrain and ephemeris datasets are included in the repository, so they do not need a separate runtime download. Unity may still fetch packages the first time the project is opened. The LFS objects must be available and hydrated; if models, terrain, or ephemeris data appear missing, run `git lfs pull` and check that Git LFS completed successfully.

## Using the application

- Enter planetocentric latitude and east-positive longitude in Site A or B, or click the Moon to place the active site. Longitude is normalized to the range [0, 360) degrees; elevations come from the LOLA reference sphere and terrain data.
- Use the site controls to focus or track a marker and configure that site's solar panel. The site panels can be expanded, scrolled, or collapsed to the left.
- The orbital panel lets you play or pause time, step forward, change the playback rate, and set a UTC epoch. A successful UTC change resets the plot and accumulated-energy windows while keeping the selected sites and their horizons.
- Time-series history starts after a site's terrain horizon is ready. Advance or play the simulation to collect samples. The CSV export includes retained samples for selected sites; it is not limited to the points currently drawn after plot decimation.
- Free-camera controls: hold the right mouse button to look around; use **WASD** to move, **Q/E** to move vertically, and **Shift** to move faster. The initial view is positioned well above the Moon and faces Earth's direction.

## Project layout

| Path | Contents |
| --- | --- |
| `Assets/Scenes/SolarSystem.unity` | Main interactive scene. |
| `Assets/Scripts/Lunar/` | LOLA sampling, lunar coordinate transforms, site placement, terrain horizons, visibility, solar power, and site UI. |
| `Assets/Scripts/Celestial/` | Ephemeris cache loading, UTC simulation time, scene animation, and Earth orientation. |
| `Assets/StreamingAssets/LOLA_LDEM64/` | Preprocessed tiled LOLA elevation data and its manifest. |
| `Assets/StreamingAssets/CelestialEphemeris/` | Runtime DE440s daily ephemeris cache and manifest. |
| `Assets/Models/`, `Assets/Generated/LunarTerrain/`, `Assets/Skybox/` | Celestial models, generated LOLA terrain meshes, and skybox assets. |
| `Tools/Ephemeris/` | Optional DE440 cache preprocessing script and the SPICE kernels it uses. |

### Main code entry points

- `LunarDEMReader` loads and bilinearly samples the preprocessed LOLA tiles.
- `LunarCoordinates` and `LunarReferenceFrame` own the scientific coordinate conversion and the explicit mapping to the Moon model.
- `LunarLocationController` and `LunarLocationPicker` route typed coordinates and Moon clicks through the same site-placement path.
- `SimulationTime`, `EphemerisDataProvider`, and `SolarSystemAnimator` own the clock, cached states, and scene animation.
- `LunarHorizonCalculator`, `LunarVisibilityCalculator`, and `SolarPowerCalculator` contain the core horizon, visibility, power, and energy calculations.
- `LunarVisibilityPresenter` owns per-site calculation state; `LunarSitePanelsUI` owns the panel presentation and plot history; `LunarTimeSeriesPlot` draws and exports the time series.

## Data and Git LFS

The terrain is based on NASA's LOLA `LDEM_64` product. The repository includes its preprocessed 276-tile signed 16-bit dataset; the original `LDEM_64.IMG` source file is not required at runtime and is not included. The ephemeris runtime uses a daily DE440s cache covering 1850-2150. SPICE kernels in `Tools/Ephemeris/kernels/` are used by the optional cache-generation script, not loaded by the Unity runtime.

Large binary assets are tracked by the root `.gitattributes` file using Git LFS. This includes the Moon/Earth/Sun models, generated terrain meshes, LOLA `.raw` tiles, the ephemeris `.bin`, the skybox image, SPICE kernels, and the handover PDF. Run `git lfs pull` after cloning or switching branches to retrieve their contents.

To regenerate the ephemeris cache, install `spiceypy` 6.0.0 in a Python environment and run `python Tools/Ephemeris/preprocess_de440_cache.py` from the repository root. The script uses the kernels under `Tools/Ephemeris/kernels/`; the checked-in runtime cache means regeneration is not needed for normal use.

## Extending the project

Keep new scientific calculations in physical units and double precision, and convert to Unity coordinates only for visualization. Route new site input through `LunarLocationController` and reuse the existing coordinate-frame path.

When adding a plot variable, keep the `LunarPlotVariable` enum aligned with `LunarPlotVariableInfo` and `LunarSitePlotHistory.ExtractValues`; also define its units, CSV token, interpolation behavior, and axis range. Adding more than two sites requires generalizing the fixed two-site state in the location controller, presenter, UI, and plot history.

## Model boundaries and validation

The rendered solar-system overview compresses the Sun-Earth distance for visibility while retaining the Earth-Moon separation. Time-dependent values use a daily ephemeris cache and interpolation intended for visualization. Solar visibility samples the apparent solar disc; Earth visibility is a binary terrain line-of-sight result. Power estimates omit effects such as eclipses, panel temperature, wiring, and hardware degradation.
