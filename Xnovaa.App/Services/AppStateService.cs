using System;
using Xnovaa.App.Models;

namespace Xnovaa.App.Services;

public interface IAppStateService
{
    AppState State { get; }
    void Load();
    void Save();
}

public class AppStateService : IAppStateService
{
    public AppState State { get; private set; } = new();

    public void Load() => State = AppPaths.Load<AppState>(AppPaths.StateFile);

    public void Save() => AppPaths.Save(AppPaths.StateFile, State);
}
