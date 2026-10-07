using Microsoft.Extensions.DependencyInjection;

namespace AttendanceCleaner;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell()) { Title = "Attendance Cleaner" };
	}
}