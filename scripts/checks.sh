# exit when any command fails
set -e -x

# TODO: linting doesn't like `ForEach` being a property. And `Fn::ForEach` doesn't support
# patterning into an array for now. See https://github.com/aws-cloudformation/cfn-language-discussion/issues/118.
#
# cfn-lint --non-zero-exit-code error templates/global.template

cfn-lint --non-zero-exit-code error templates/regional.template
cfn-lint --non-zero-exit-code error templates/api-gateway.template