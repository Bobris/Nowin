nuget pack -Build -Symbols -Properties Configuration=Release Nowin.csproj
nuget push *.nupkg
