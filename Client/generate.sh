VERSION=0.1.0

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER})  openapitools/openapi-generator-cli generate \
-i http://localhost:5024/swagger/v1/swagger.json \
-g csharp-netcore \
-o /local/out --additional-properties=packageName=Coflnet.Sky.EventBroker.Client,packageVersion=$VERSION,licenseId=MIT

cd out
sed -i 's/GIT_USER_ID/Coflnet/g' src/Coflnet.Sky.EventBroker.Client/Coflnet.Sky.EventBroker.Client.csproj
sed -i 's/GIT_REPO_ID/SkyBase/g' src/Coflnet.Sky.EventBroker.Client/Coflnet.Sky.EventBroker.Client.csproj
sed -i 's/>OpenAPI/>Coflnet/g' src/Coflnet.Sky.EventBroker.Client/Coflnet.Sky.EventBroker.Client.csproj

dotnet pack
cp src/Coflnet.Sky.EventBroker.Client/bin/Debug/Coflnet.Sky.EventBroker.Client.*.nupkg ..
