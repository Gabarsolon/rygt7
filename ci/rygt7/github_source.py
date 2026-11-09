"""Resolve public GitHub commits and verify exact revision overrides."""
import json
import re
import urllib.request

SHA = re.compile(r'^[0-9a-f]{40}$')

def resolve_commit(api, revision):
    if revision != 'main' and not SHA.fullmatch(revision or ''):
        raise ValueError('Expected main or a full lowercase Git commit SHA')
    request = urllib.request.Request(api + '/commits/' + revision,
                                     headers={'User-Agent': 'RyGT7-CI', 'Accept': 'application/vnd.github+json'})
    with urllib.request.urlopen(request, timeout=60) as response:
        actual = json.load(response)['sha']
    if not isinstance(actual, str) or not SHA.fullmatch(actual) or (revision != 'main' and actual != revision):
        raise ValueError('GitHub returned a different or invalid commit')
    return actual
