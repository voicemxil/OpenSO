# Registration modes: public vs invite-only

OpenSO's account registration runs in one of two modes, decided entirely by whether the API config has a
`regkey` set. See `Registration.md` for the full endpoint reference; this document covers only how the two
modes differ and how each client surface adapts.

## The two modes

| | Public (default) | Invite-only |
|---|---|---|
| Config | `regkey` absent/empty | `regkey` set to a secret string |
| Who can register | anyone | only someone who knows the key |
| Server enforcement | none | `key` form field must equal `regkey`, else `key_wrong` |

The **current OpenSO deployment uses public mode.** Invite-only is fully wired but off. Turning it on is a
one-line config change (`regkey`) with no client redeploy required — the clients discover the mode at runtime.

## How the server decides

`RegistrationController` compares the submitted `key` form field against `api.Config.Regkey` in **both**
account-creation endpoints:

- `POST userapi/registration` (direct, no-SMTP flow)
- `POST userapi/registration/confirm` (email-verification flow — this is the one OpenSO uses)

If `Regkey` is non-empty and the submitted `key` doesn't match, the endpoint returns
`{ error: "key_wrong" }` and no account is created. The `key` field has always existed on these endpoints;
what was missing (and is fixed here) was any way for the in-game client and website to *discover* that a key
is needed and to *collect and submit* one.

## Discovery: `GET userapi/registration/info`

A new unauthenticated endpoint reports the registration mode as booleans only:

```json
{ "smtp_enabled": true, "key_required": false }
```

- `key_required` = `regkey` is set (invite-only). **The key itself is never returned** — only whether one is
  needed. No secret is exposed anywhere a client can read it.
- `smtp_enabled` = the server uses the email-verification flow.

### Fail-open contract (critical)

Every client treats an unreachable, absent, or malformed `/info` response as `key_required: false`
(public mode). The discovery call is always **non-blocking** and **fire-and-forget**: the registration form
is fully usable before it returns and even if it never does. This guarantees:

- Public mode is byte-for-byte unchanged — no key field, no extra required input, identical request bodies.
- An older server without `/info`, or a transient network failure, can never break registration.
- The server remains the source of truth: it still enforces `regkey` at submit time regardless of what the
  client believed. If discovery wrongly said "no key," the submit returns `key_wrong` and the client then
  reveals the key field so the user can supply it (in-game) or sees a friendly error (website).

## What each surface does

### Server (`FSO.Server.Api.Core`)
- `RegistrationController.GetInfo` — the discovery endpoint above.
- Key validation unchanged in `CreateUser` / `CreateUserWithToken`.
- Verification and password-reset emails are rate limited per email address: a durable, DB-backed windowed
  cap on send *attempts* (`fso_email_send_log`), layered on top of the between-send cooldown. Over the cap the
  request returns `email_rate_limited` and no email is sent.

### In-game client (`tso.client` + `FSO.Server.Clients`)
- `RegistrationClient.GetInfo` fetches discovery (fail-open) when `UIRegistrationDialog` opens.
- If `key_required`, the dialog reveals a **Registration key** field in step 2 (alongside username/password,
  where it is submitted) and grows to fit it. `RegistrationClient.ConfirmCode` sends the `key` form field.
- Safety net: if discovery failed but the server actually requires a key, the first `confirm` returns
  `key_wrong`; the dialog then reveals the key field so the user can retry inline — never a dead end.
- In public mode the field never appears; the dialog is identical to before.

### Website (`openso-website`)
- `register.html` / `confirm.html` call `opensoRegInfo()` (fail-open) on load.
- Email-verification flow (OpenSO's mode): the key field lives on `confirm.html` (submitted with `/confirm`).
  `register.html` shows a short invite-only notice so the user expects it. (The key can't ride the email link
  — the link may be opened on another device, and a key doesn't belong in a URL.)
- Direct/no-SMTP flow: the key field appears on `register.html` and is submitted with `/userapi/registration`.
- `key_wrong` maps to a friendly, user-facing message in `assets/openso.js`.
- In public mode no key field or notice is shown; request bodies are identical to before.

## The key is never in a client

The registration key is a server secret. It lives only in the server's API config and is compared
server-side. No client binary, page, or API response ever contains it — clients learn only the boolean
`key_required`. An operator distributes the key to invitees out of band (Discord, email, etc.).
