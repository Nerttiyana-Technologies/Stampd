RESP=$(curl -s -X POST $BASE_URL/api/signing-requests \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"documentTemplateId\": \"$TEMPLATE_ID\",
    \"subject\": \"Designer-built template test\",
    \"recipients\": [{\"roleName\":\"Signer\",\"email\":\"alice@example.com\",\"name\":\"Alice\"}]
  }")

ACCESS_TOKEN=$(echo $RESP | jq -r '.recipients[0].accessUrl' | sed 's|.*/||')
echo "Open: http://localhost:5170/sign/$ACCESS_TOKEN"
