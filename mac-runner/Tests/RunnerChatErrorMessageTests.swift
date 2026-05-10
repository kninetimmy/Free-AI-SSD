import Foundation

@main
struct RunnerChatErrorMessageTests {
    static func main() {
        var failures = 0

        // BadRequest from RunnerLocalApiService (validation): {"error": "..."}.
        let badRequest = #"{"error":"Model is required."}"#.data(using: .utf8)!
        assertEq(
            RunnerChatErrorMessage.decode(statusCode: 400, body: badRequest),
            "Model is required.",
            label: "400 ErrorResponse → error string",
            failures: &failures)

        // Results.Problem (ASP.NET ProblemDetails): {"detail": "...", "status": 503}.
        let problem = #"{"type":"https://tools.ietf.org/html/rfc7231#section-6.6.4","title":"Service Unavailable","status":503,"detail":"Service unavailable."}"#.data(using: .utf8)!
        assertEq(
            RunnerChatErrorMessage.decode(statusCode: 503, body: problem),
            "Service unavailable.",
            label: "503 ProblemDetails → detail string",
            failures: &failures)

        // detail beats error when both are present.
        let both = #"{"detail":"detail wins.","error":"error loses."}"#.data(using: .utf8)!
        assertEq(
            RunnerChatErrorMessage.decode(statusCode: 500, body: both),
            "detail wins.",
            label: "detail takes precedence over error",
            failures: &failures)

        // Empty body → fallback.
        assertEq(
            RunnerChatErrorMessage.decode(statusCode: 500, body: Data()),
            "API returned 500.",
            label: "500 empty body → fallback",
            failures: &failures)

        // Non-JSON garbage → fallback (no crash).
        let garbage = "<html>500 Internal Server Error</html>".data(using: .utf8)!
        assertEq(
            RunnerChatErrorMessage.decode(statusCode: 502, body: garbage),
            "API returned 502.",
            label: "non-JSON body → fallback",
            failures: &failures)

        // nil body → fallback (defensive — happens if URLSession surfaces no body).
        assertEq(
            RunnerChatErrorMessage.decode(statusCode: 0, body: nil),
            "API returned 0.",
            label: "nil body → fallback",
            failures: &failures)

        // Whitespace-only error/detail strings fall through to the fallback so
        // we don't show the user a blank red banner.
        let whitespace = #"{"error":"   "}"#.data(using: .utf8)!
        assertEq(
            RunnerChatErrorMessage.decode(statusCode: 400, body: whitespace),
            "API returned 400.",
            label: "whitespace-only error string → fallback",
            failures: &failures)

        if failures > 0 {
            print("RunnerChatErrorMessageTests: \(failures) failure(s).")
            exit(1)
        }
        print("RunnerChatErrorMessageTests: all pass.")
    }

    static func assertEq(_ actual: String, _ expected: String, label: String, failures: inout Int) {
        if actual == expected {
            print("PASS  \(label)")
        } else {
            print("FAIL  \(label)\n      expected: \(expected)\n      actual:   \(actual)")
            failures += 1
        }
    }
}
