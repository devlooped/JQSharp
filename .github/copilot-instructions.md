# .NET SDK-based Repository

**Always reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the info here.**

## Working Effectively

### Essential Build Commands
- **Restore dependencies**: `dotnet restore`

- **Build the entire solution**: `dotnet build`

- **Run tests**: `dnx --yes retest`
  - Runs all unit tests across the solution

### Project Structure and Navigation

The codebase is documented in .github/design.md, use it to understand the design and implementation details and keep it up to date as you make changes.

### Code Style and Formatting

#### EditorConfig Rules
The repository uses `.editorconfig` for consistent code style:
- **Indentation**: 4 spaces for C# files, 2 spaces for XML/YAML/JSON
- **Line endings**: LF (Unix-style)
- **Sort using directives**: System.* namespaces first (`dotnet_sort_system_directives_first = true`)
- **Type references**: Prefer language keywords over framework type names (`int` vs `Int32`)
- **Modern C# features**: Use object/collection initializers, coalesce expressions when possible

#### Formatting Validation
- CI enforces formatting with `dotnet format whitespace` and `dotnet format style`
- Run locally: `dotnet format whitespace --verify-no-changes -v:diag --exclude ~/.nuget`
- Fix formatting: `dotnet format` (without `--verify-no-changes`)

### Testing Practices

#### Test Framework
- **xUnit** for all unit and integration tests
- **Moq** for mocking dependencies
- Located in `src/Tests/`

#### Running Tests
- Full test suite: `dnx --yes retest` (NEVER cancel this - it's the CI validation command)
- With dotnet test: `dotnet test --no-build` (after building)

### Dependency Management

#### Key Dependencies
- **xUnit** - Testing framework
- **Moq** - Mocking framework

#### Adding Dependencies
- Add to appropriate `.csproj` file
- Run `dotnet restore` to update dependencies
- Ensure version consistency across projects where applicable

### Common Workflows and Troubleshooting

#### Build Issues
- **Missing types**: Ensure `dotnet restore` completed successfully

### Special Files and Tools

#### dnx Command
- **Purpose**: Runs custom dotnet tools from nuget.org
- **Usage**: `dnx --yes retest` - runs tests with automatic retry on transient failures
- **In CI**: `dnx --yes retest -- --no-build` (skips build, runs tests only)

#### Code Quality
- All PRs must pass format validation
- Tests must pass on all target frameworks
- Follow existing patterns and conventions in the codebase
