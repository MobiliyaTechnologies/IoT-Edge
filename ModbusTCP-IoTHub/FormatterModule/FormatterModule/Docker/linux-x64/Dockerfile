FROM microsoft/dotnet:2.0.0-runtime

ARG EXE_DIR=.

WORKDIR /app

COPY $EXE_DIR/ ./

CMD ["dotnet", "FormatterModule.dll"]