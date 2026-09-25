# drive-in.online

Placeholder site for [drive-in.online](https://drive-in.online): turn-key software for drive-in
theaters (ticket sales, lot capacity, concessions, and more).

## Layout

| Path | What |
|---|---|
| `site/` | The website: plain HTML/CSS/JS, no build step. Everything here is published. |
| `infra/dns.yml` | CloudFormation: Route 53 hosted zone for drive-in.online. |
| `infra/site.yml` | CloudFormation: S3 bucket, CloudFront, ACM cert, alias records, GitHub deploy role. |
| `.github/workflows/deploy.yml` | Deploys `site/` to S3 and invalidates CloudFront on every merge to `main`. |

## Workflow

All changes go through a feature branch and a pull request; nothing is committed to `main` directly.

1. `git checkout -b feature/whatever`
2. Edit files in `site/`; preview with any static server, e.g. `python -m http.server -d site`.
3. Push, open a PR, merge to `main` → GitHub Actions deploys within a minute or two.

## How deploys authenticate

GitHub Actions assumes the IAM role `drive-in-github-deploy` via OIDC (no stored AWS keys).
The role trusts only this repo's `main` branch, using GitHub's immutable subject format
(`repo:owner@id/repo@id:ref:refs/heads/main`), and can only write to the site bucket and
invalidate the distribution. The workflow reads these repo **variables**:

- `AWS_ROLE_ARN`, `S3_BUCKET`, `CF_DISTRIBUTION_ID` (from the `drive-in-site` stack outputs)

The GitHub OIDC provider itself is shared and owned by the `magicworld-site` stack.

## Infrastructure (one-time / rare changes)

Both stacks are in `us-east-1`. Deploy `site.yml` only after the domain's nameservers point
to Route 53, since the certificate is validated through DNS.

```sh
aws cloudformation deploy --region us-east-1 --stack-name drive-in-dns  --template-file infra/dns.yml
aws cloudformation deploy --region us-east-1 --stack-name drive-in-site --template-file infra/site.yml \
  --capabilities CAPABILITY_NAMED_IAM
```

The domain is **registered** at GoDaddy; its nameservers point to Route 53. It has no email.
