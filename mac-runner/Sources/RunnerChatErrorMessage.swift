import Foundation

/// M12: pure helper for decoding HTTP error bodies returned by
/// `mac-runner-host`'s `/api/chat/stream`. Kept out of `RunnerViewModel`
/// so the standalone test binary can pin it without linking SwiftUI/AppKit.
///
/// `RunnerLocalApiService` returns two JSON shapes for chat failures:
///   - `Results.BadRequest(new ErrorResponse(error))` → `{"error": "..."}`
///   - `Results.Problem(detail, statusCode)` → `{"detail": "...", ...}`
/// `detail` is checked first so ASP.NET ProblemDetails resolves through.
enum RunnerChatErrorMessage {
    static func decode(statusCode: Int, body: Data?) -> String {
        if let body,
           let obj = try? JSONSerialization.jsonObject(with: body) as? [String: Any] {
            if let detail = obj["detail"] as? String,
               !detail.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return detail
            }
            if let error = obj["error"] as? String,
               !error.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return error
            }
        }
        return "API returned \(statusCode)."
    }
}
