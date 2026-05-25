# Contributing to BrainX

Thanks for your interest in contributing!

## Quick Start

```bash
git clone https://github.com/xjanova/BrainX.git
cd BrainX
dotnet build BrainX.slnx
```

## Project Structure

| Project | What it does |
|---------|-------------|
| `BrainX.Core` | Shared models, knowledge indexer, Claude integration |
| `BrainX.Client` | WPF desktop app with 3D brain visualization |
| `BrainX.Server` | ASP.NET Core + SignalR matchmaking hub |

## Development Workflow

1. Fork the repo
2. Create a feature branch from `main`
3. Make your changes
4. Ensure `dotnet build BrainX.slnx` passes
5. Submit a PR

## Branch Naming

- `feature/description` — New features
- `fix/description` — Bug fixes
- `perf/description` — Performance improvements
- `ui/description` — UI/UX changes

## Code Guidelines

- C# naming: PascalCase public, _camelCase private fields
- Physics code stays in `PhysicsEngine.cs`
- Rendering code stays in `MainWindow.xaml.cs`
- All models go in `BrainX.Core/Models/`
- Keep the 3D rendering performant (batch meshes, use LOD)

## Testing Performance

When changing rendering or physics code, test with large vaults (100+ nodes):
- FPS should stay above 30
- No visible lag on camera drag/zoom
- Memory should not grow unbounded
