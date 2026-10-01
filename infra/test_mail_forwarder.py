"""Tests for the mail forwarder Lambda inlined in mail.yml. Run: python infra/test_mail_forwarder.py"""
import io
import os
import sys
import types
import unittest
from email import policy
from email.parser import BytesParser

TEMPLATE = os.path.join(os.path.dirname(__file__), "mail.yml")


def forwarder_source():
    """The Lambda's code: the indented block under `ZipFile: |` in mail.yml."""
    with open(TEMPLATE, encoding="utf-8") as f:
        lines = f.read().splitlines()
    start = next(i for i, line in enumerate(lines) if line.strip() == "ZipFile: |") + 1
    indent = len(lines[start]) - len(lines[start].lstrip())
    body = []
    for line in lines[start:]:
        if line.strip() and len(line) - len(line.lstrip()) < indent:
            break
        body.append(line[indent:])
    return "\n".join(body).rstrip() + "\n"


class FakeClients:
    def __init__(self, raw):
        self.raw, self.sent = raw, []

    def client(self, name):
        if name == "s3":
            return types.SimpleNamespace(get_object=lambda **kw: {"Body": io.BytesIO(self.raw)})
        return types.SimpleNamespace(send_email=lambda **kw: self.sent.append(kw))


def load(raw=b""):
    fake = FakeClients(raw)
    sys.modules["boto3"] = types.SimpleNamespace(client=fake.client)
    module = types.ModuleType("index")
    exec(compile(forwarder_source(), "index.py", "exec"), module.__dict__)
    return module, fake


RAW = (b"From: Pat Buyer <pat@example.com>\r\nTo: info@drive-in.online\r\nSubject: Hello\r\n"
       b"Message-ID: <abc@example.com>\r\nDKIM-Signature: v=1; d=example.com; b=xyz\r\n"
       b"Return-Path: <pat@example.com>\r\n\r\nIs Friday's show on?\r\n")


def event(source="pat@example.com", spam="PASS", virus="PASS"):
    return {"Records": [{"ses": {
        "mail": {"messageId": "m1", "source": source},
        "receipt": {"recipients": ["info@drive-in.online"], "spamVerdict": {"status": spam},
                    "virusVerdict": {"status": virus}}}}]}


class ForwarderTests(unittest.TestCase):
    def setUp(self):
        os.environ.update(BUCKET="b", FORWARD_TO="me@example.net", FROM_ADDRESS="forwarder@drive-in.online",
                          DOMAIN="drive-in.online")

    def test_fits_cloudformations_inline_limit(self):
        self.assertLess(len(forwarder_source()), 4096)

    def test_rewrite_sends_from_us_with_the_sender_in_reply_to(self):
        module, _ = load()
        out = BytesParser(policy=policy.SMTP).parsebytes(
            module.rewrite(RAW, ["info@drive-in.online"], "forwarder@drive-in.online", "drive-in.online"))
        self.assertEqual(out["From"], '"Pat Buyer via drive-in.online" <forwarder@drive-in.online>')
        self.assertEqual(out["Reply-To"], "Pat Buyer <pat@example.com>")
        self.assertEqual(out["To"], "info@drive-in.online")
        self.assertEqual(out["X-Original-To"], "info@drive-in.online")
        self.assertEqual(out["Subject"], "Hello")
        for header in ("DKIM-Signature", "Message-ID", "Return-Path"):
            self.assertIsNone(out[header], header)
        self.assertIn("Is Friday's show on?", out.get_content())

    def test_rewrite_keeps_an_existing_reply_to(self):
        module, _ = load()
        raw = RAW.replace(b"Subject:", b"Reply-To: tickets@example.com\r\nSubject:")
        out = BytesParser(policy=policy.SMTP).parsebytes(
            module.rewrite(raw, ["a@drive-in.online"], "forwarder@drive-in.online", "drive-in.online"))
        self.assertEqual(out["Reply-To"], "tickets@example.com")

    def test_handler_forwards_to_the_mailbox(self):
        module, fake = load(RAW)
        module.handler(event(), None)
        sent = fake.sent[0]
        self.assertEqual(sent["FromEmailAddress"], "forwarder@drive-in.online")
        self.assertEqual(sent["Destination"], {"ToAddresses": ["me@example.net"]})
        self.assertIn(b"Reply-To: Pat Buyer <pat@example.com>", sent["Content"]["Raw"]["Data"])

    def test_handler_drops_spam_viruses_and_its_own_forwards(self):
        for e in (event(spam="FAIL"), event(virus="FAIL"), event(source="Forwarder@drive-in.online")):
            module, fake = load(RAW)
            module.handler(e, None)
            self.assertEqual(fake.sent, [])


if __name__ == "__main__":
    unittest.main()
