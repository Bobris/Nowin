nuget pack -Build -Symbols -Properties Configuration=Release
nuget push *.nupkg
