import XCTest
@testable import iTraderCore

/// RpcError.requestFailed 对启动拦截问题列表的解析。
final class RpcErrorIssuesTests: XCTestCase {
    func testIssuesParsedForEngineStartBlocked() {
        let error = RpcError.requestFailed(
            "账户「A」未配置快期账号；本地 API token 未领取",
            "EngineStartBlockedError",
            ["issues": ["账户「A」未配置快期账号", "本地 API token 未领取"]]
        )
        XCTAssertEqual(error.issues, ["账户「A」未配置快期账号", "本地 API token 未领取"])
    }

    func testIssuesEmptyForOtherErrorTypes() {
        let error = RpcError.requestFailed("boom", "RuntimeError", ["issues": ["不该被读取"]])
        XCTAssertTrue(error.issues.isEmpty)
    }

    func testIssuesEmptyWhenDataMissing() {
        let error = RpcError.requestFailed("boom", "EngineStartBlockedError", [:])
        XCTAssertTrue(error.issues.isEmpty)
    }

    func testErrorDescriptionHidesTypeForRuntimeErrors() {
        let runtime = RpcError.requestFailed("服务器未连接", "RuntimeError", [:])
        XCTAssertEqual(runtime.errorDescription, "服务器未连接")

        let blocked = RpcError.requestFailed("问题摘要", "EngineStartBlockedError", [:])
        XCTAssertEqual(blocked.errorDescription, "EngineStartBlockedError: 问题摘要")
    }
}
