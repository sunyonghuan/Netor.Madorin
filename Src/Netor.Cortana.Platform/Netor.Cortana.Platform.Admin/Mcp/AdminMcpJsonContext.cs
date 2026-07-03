using System.Text.Json.Serialization;
using Netor.Cortana.Platform.Admin.Mcp.Tools;
using Netor.Cortana.Platform.Admin.Operations;

namespace Netor.Cortana.Platform.Admin.Mcp;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(AccountSetStatusInput))]
[JsonSerializable(typeof(AccountRechargeWalletInput))]
[JsonSerializable(typeof(AccountSearchInput))]
[JsonSerializable(typeof(AccountGetInput))]
[JsonSerializable(typeof(AccountListTransactionsInput))]
[JsonSerializable(typeof(AssetSearchInput))]
[JsonSerializable(typeof(AssetGetInput))]
[JsonSerializable(typeof(AssetSetFeaturedInput))]
[JsonSerializable(typeof(AssetSetStatusInput))]
[JsonSerializable(typeof(DocsCategoriesListInput))]
[JsonSerializable(typeof(DocsCategoryUpsertInput))]
[JsonSerializable(typeof(DocsArticlesSearchInput))]
[JsonSerializable(typeof(DocsArticleGetInput))]
[JsonSerializable(typeof(DocsArticleUpsertInput))]
[JsonSerializable(typeof(DocsArticleSetStatusInput))]
[JsonSerializable(typeof(DocsImageUploadInput))]
[JsonSerializable(typeof(OperationResult<AccountStatusResult>))]
[JsonSerializable(typeof(OperationResult<AccountRechargeResult>))]
[JsonSerializable(typeof(OperationResult<AccountSearchResult>))]
[JsonSerializable(typeof(OperationResult<AccountDetailResult>))]
[JsonSerializable(typeof(OperationResult<AccountTransactionSearchResult>))]
[JsonSerializable(typeof(OperationResult<AssetSearchResult>))]
[JsonSerializable(typeof(OperationResult<AssetDetailResult>))]
[JsonSerializable(typeof(OperationResult<AssetFeaturedResult>))]
[JsonSerializable(typeof(OperationResult<AssetStatusResult>))]
[JsonSerializable(typeof(OperationResult<DashboardSummaryResult>))]
[JsonSerializable(typeof(OperationResult<DocCategorySearchResult>))]
[JsonSerializable(typeof(OperationResult<DocCategoryResult>))]
[JsonSerializable(typeof(OperationResult<DocArticleSearchResult>))]
[JsonSerializable(typeof(OperationResult<DocArticleDetailResult>))]
[JsonSerializable(typeof(OperationResult<DocArticleStatusResult>))]
[JsonSerializable(typeof(OperationResult<DocImageUploadResult>))]
internal sealed partial class AdminMcpJsonContext : JsonSerializerContext;
