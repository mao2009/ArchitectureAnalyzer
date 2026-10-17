"""Offline, deterministic tests for solo-maintainer SHA-bound acceptance."""
import importlib.util
import pathlib
import unittest

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "scripts" / "solo_maintainer_approval.py"
SPEC = importlib.util.spec_from_file_location("solo_maintainer_approval", SOURCE)
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)

HEAD = "a" * 40
NEXT = "b" * 40
REPO = "mao2009/PolicySharp"


def pr(head=HEAD, labels=("policy-approved",), state="open", base="main"):
    return {"head": {"sha": head}, "labels": [{"name": v} for v in labels],
            "base": {"ref": base}, "state": state}


def approval(sha=HEAD, author="mao2009", association="OWNER", body=None):
    return {"user": {"login": author}, "author_association": association,
            "body": body if body is not None else "SOLO-APPROVED " + sha}


class SoloMaintainerApprovalTests(unittest.TestCase):
    def valid(self, pull=None, comments=None, label="policy-approved"):
        return gate.is_approved(pull if pull is not None else pr(),
                                comments if comments is not None else [approval()],
                                owner="mao2009", expected_sha=HEAD, required_label=label)

    def test_explicit_owner_sha_is_accepted(self):
        self.assertTrue(self.valid())

    def test_wrong_or_stale_commit_denied(self):
        self.assertFalse(self.valid(comments=[approval(NEXT)]))
        self.assertFalse(self.valid(pull=pr(head=NEXT)))

    def test_author_must_be_owner(self):
        self.assertFalse(self.valid(comments=[approval(author="other-user")]))
        self.assertFalse(self.valid(comments=[approval(association="CONTRIBUTOR")]))

    def test_not_a_pr_body_or_false_label(self):
        self.assertFalse(self.valid(comments=[]))
        self.assertFalse(self.valid(comments=[approval(body="The agent says SOLO-APPROVED " + HEAD)]))
        self.assertFalse(self.valid(pull=pr(labels=())))
        self.assertTrue(self.valid(label=""))

    def test_requires_open_pr_to_main(self):
        self.assertFalse(self.valid(pull=pr(state="closed")))
        self.assertFalse(self.valid(pull=pr(base="develop")))

    def test_exact_command_no_whitespace_injection(self):
        self.assertFalse(self.valid(comments=[approval(body="SOLO-APPROVED " + HEAD + " and merge")]))

    def test_api_check_stale_head_fails_without_reading_comments(self):
        calls = []
        def fetch(path, *, token):
            calls.append(path)
            return pr(head=NEXT)
        self.assertFalse(gate.check(REPO, 43, HEAD, "mao2009", "read-token",
                                    required_label="policy-approved", fetch=fetch))
        self.assertEqual(1, len(calls))

    def test_api_check_paginates_and_accepts_only_owner_comment(self):
        calls = []
        def fetch(path, *, token):
            calls.append(path)
            if path.endswith("/pulls/43"):
                return pr()
            if path.endswith("&page=1"):
                return [approval(author="bot")] * 100
            return [approval()]
        self.assertTrue(gate.check(REPO, 43, HEAD, "mao2009", "read-token",
                                   required_label="policy-approved", fetch=fetch))
        self.assertEqual(3, len(calls))

    def test_unreadable_api_or_invalid_owner_fails_closed(self):
        with self.assertRaises(ValueError):
            gate.check(REPO, 43, HEAD, "someone-else", "read-token", fetch=lambda *a, **k: {})
        with self.assertRaises(ValueError):
            gate.check(REPO, 43, HEAD, "mao2009", "",
                       fetch=lambda *a, **k: {})
        with self.assertRaises(ValueError):
            gate.check(REPO, 43, HEAD, "mao2009", "read-token",
                       fetch=lambda *a, **k: [])


if __name__ == "__main__":
    unittest.main()
