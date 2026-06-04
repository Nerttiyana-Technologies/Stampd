BASE_URL=http://localhost:5070
TOKEN=$(curl -s -X POST $BASE_URL/api/auth/dev-token \
  -H "Content-Type: application/json" \
  -d '{"subject":"designer-user"}' | jq -r .accessToken | tr -d '\n')
echo $TOKEN | wc -c     # ~240 chars
echo "$TOKEN" | pbcopy  # macOS: token on clipboard, no trailing newlin
