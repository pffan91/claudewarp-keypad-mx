# source this before any dotnet/logiplugintool work in this project
#
# Homebrew's dotnet formula installs outside the default /usr/local/share/dotnet, so DOTNET_ROOT has
# to be explicit. And LogiPluginTool 6.1.4 (June 2025) is built for net8.0 while only the .NET 10
# runtime is installed, so it needs roll-forward to launch at all.
export PATH="/opt/homebrew/bin:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
export DOTNET_ROLL_FORWARD=Major
